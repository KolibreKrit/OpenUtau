using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Xunit;

namespace OpenUtau.Core.DawIntegration {
    /// <summary>
    /// Saves run through <see cref="DocManager"/> with a plugin connected, as File > Save and
    /// Save As run them. A folder that doesn't exist stands in for one that can't be written to.
    /// </summary>
    [Collection(DawIntegrationCollection.Name)]
    public class DawSaveTest : IDisposable {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly string directory;
        private readonly DawTestPlugin plugin;
        private readonly Thread previousMainThread;
        private readonly UProject previousProject;
        private readonly string previousRecoveryPath;
        private DawManager? manager;
        private DateTime now = T0;

        public DawSaveTest() {
            directory = Path.Combine(Path.GetTempPath(), "OpenUtauDawSaveTest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            plugin = DawTestPlugin.Start("SavePlugin", Path.Combine(directory, "discovery"));
            previousMainThread = DocManager.Inst.TakeMainThreadForTest(Thread.CurrentThread);
            previousProject = DocManager.Inst.Project;
            previousRecoveryPath = Preferences.Default.RecoveryPath;
        }

        public void Dispose() {
            manager?.Dispose();
            plugin.Dispose();
            Execute(new LoadProjectNotification(previousProject));
            DocManager.Inst.TakeMainThreadForTest(previousMainThread);
            // A save that works clears the recovery file OpenUtau offers at startup.
            Preferences.Default.RecoveryPath = previousRecoveryPath;
            Preferences.Save();
            try {
                Directory.Delete(directory, recursive: true);
            } catch (Exception) {
                // A leftover temp directory is not worth failing a test over.
            }
        }

        /// <summary>Runs a command as the UI thread does, inline on this thread.</summary>
        private static void Execute(UCommand cmd) {
            DocManager.Inst.TakeMainThreadForTest(Thread.CurrentThread);
            DocManager.Inst.ExecuteCmd(cmd);
        }

        /// <summary>A saved project, open in OpenUtau, with a plugin connected. Connecting subscribes the manager to every command.</summary>
        private async Task<DawManager> ConnectSavedProjectAsync(string path) {
            Execute(new LoadProjectNotification(Format.Ustx.Create()));
            Execute(new SaveProjectNotification(path));
            manager = new DawManager(null) {
                UseTimerPump = false,
                NowUtc = () => now,
                ProjectSource = () => DocManager.Inst.Project,
            };
            await manager.ConnectAsync(plugin.Advertisement);
            return manager;
        }

        [Fact]
        public async Task FailedSaveAsSendsNothing() {
            string savedPath = Path.Combine(directory, "song.ustx");
            var openutau = await ConnectSavedProjectAsync(savedPath);
            int before = plugin.Received.Count;
            int projectInfos = plugin.Received.Count(kind => kind == DawMessageKind.UpdateProjectInfo);

            Execute(new SaveProjectNotification(Path.Combine(directory, "missing", "renamed.ustx")));

            Assert.False(openutau.Scheduler.HasPending);
            Assert.Equal(savedPath, DocManager.Inst.Project.FilePath);

            // Save writes the file the plugin already knows, which changes only the project info.
            Execute(new SaveProjectNotification(""));
            now += DawSyncScheduler.DefaultSlowDebounce;
            await openutau.PumpOnceAsync();
            await plugin.WaitForCountAsync(DawMessageKind.UpdateProjectInfo, projectInfos + 1);

            Assert.Equal(new[] { DawMessageKind.UpdateProjectInfo }, plugin.Received.Skip(before).ToArray());
        }
    }
}
