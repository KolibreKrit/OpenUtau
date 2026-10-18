using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Xunit;

namespace OpenUtau.Core {
    /// <summary>
    /// Saves run through <see cref="DocManager"/>, as File > Save and Save As run them. A folder
    /// that doesn't exist stands in for one that can't be written to.
    /// </summary>
    [Collection(RenderSingletonCollection.Name)]
    public class SaveProjectTest : IDisposable {
        readonly string directory;
        readonly string savedPath;
        readonly string unwritablePath;
        readonly Thread previousMainThread;
        readonly UProject previousProject;
        readonly string previousRecoveryPath;
        readonly Recorder recorder = new Recorder();

        sealed class Recorder : ICmdSubscriber {
            public readonly List<UCommand> Commands = new List<UCommand>();
            public void OnNext(UCommand cmd, bool isUndo) => Commands.Add(cmd);
        }

        sealed class Edit : UCommand {
            public override void Execute() { }
            public override void Unexecute() { }
            public override string ToString() => "edit";
        }

        public SaveProjectTest() {
            directory = Path.Combine(Path.GetTempPath(), "OpenUtauSaveTest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            savedPath = Path.Combine(directory, "song.ustx");
            unwritablePath = Path.Combine(directory, "missing", "renamed.ustx");
            previousMainThread = DocManager.Inst.TakeMainThreadForTest(Thread.CurrentThread);
            previousProject = DocManager.Inst.Project;
            previousRecoveryPath = Preferences.Default.RecoveryPath;
            Execute(new LoadProjectNotification(Format.Ustx.Create()));
            DocManager.Inst.AddSubscriber(recorder);
        }

        public void Dispose() {
            DocManager.Inst.RemoveSubscriber(recorder);
            Execute(new LoadProjectNotification(previousProject));
            DocManager.Inst.TakeMainThreadForTest(previousMainThread);
            // A save that works clears the recovery file OpenUtau offers at startup, and autosave sets it.
            Preferences.Default.RecoveryPath = previousRecoveryPath;
            Preferences.Save();
            try {
                Directory.Delete(directory, recursive: true);
            } catch (Exception) {
                // A leftover temp directory is not worth failing a test over.
            }
        }

        /// <summary>Runs a command as the UI thread does, inline on this thread.</summary>
        static void Execute(UCommand cmd) {
            DocManager.Inst.TakeMainThreadForTest(Thread.CurrentThread);
            DocManager.Inst.ExecuteCmd(cmd);
        }

        /// <summary>Save As with a path, or Save with an empty one. True if the file was written.</summary>
        static bool Save(string path) {
            var save = new SaveProjectNotification(path);
            Execute(save);
            return save.Saved;
        }

        static void MakeEdit() {
            DocManager.Inst.TakeMainThreadForTest(Thread.CurrentThread);
            DocManager.Inst.StartUndoGroup();
            DocManager.Inst.ExecuteCmd(new Edit());
            DocManager.Inst.EndUndoGroup();
        }

        static UProject Project => DocManager.Inst.Project;

        bool SaveWasPublished => recorder.Commands.Exists(cmd => cmd is SaveProjectNotification);

        [Fact]
        public void FailedSaveAsLeavesTheProjectWhereItWas() {
            Save(savedPath);
            MakeEdit();
            recorder.Commands.Clear();

            Assert.False(Save(unwritablePath));

            Assert.Equal(savedPath, Project.FilePath);
            Assert.True(Project.Saved);
            Assert.False(DocManager.Inst.ChangesSaved);
            Assert.False(File.Exists(unwritablePath));
            // The error shows, and nothing hears of a save.
            Assert.Contains(recorder.Commands, cmd => cmd is ErrorMessageNotification);
            Assert.False(SaveWasPublished);

            // Save still writes the file the project came from.
            File.Delete(savedPath);
            Assert.True(Save(""));
            Assert.True(File.Exists(savedPath));
            Assert.True(DocManager.Inst.ChangesSaved);
            Assert.True(SaveWasPublished);
        }

        [Fact]
        public void FailedSaveKeepsChangesUnsaved() {
            Save(savedPath);
            MakeEdit();
            recorder.Commands.Clear();
            // The project's folder has gone, as with a removed drive.
            Directory.Delete(directory, recursive: true);

            Assert.False(Save(""));

            Assert.Equal(savedPath, Project.FilePath);
            Assert.False(DocManager.Inst.ChangesSaved);
            Assert.Contains(recorder.Commands, cmd => cmd is ErrorMessageNotification);
            Assert.False(SaveWasPublished);

            Directory.CreateDirectory(directory);
            Assert.True(Save(""));
            Assert.True(File.Exists(savedPath));
            Assert.True(DocManager.Inst.ChangesSaved);
            Assert.True(SaveWasPublished);
        }

        [Fact]
        public void FailedFirstSaveLeavesTheProjectUntitled() {
            MakeEdit();

            Assert.False(Save(unwritablePath));

            Assert.Equal(string.Empty, Project.FilePath);
            Assert.False(Project.Saved);
            Assert.False(DocManager.Inst.ChangesSaved);
            Assert.False(SaveWasPublished);

            Assert.True(Save(savedPath));
            Assert.Equal(savedPath, Project.FilePath);
            Assert.True(Project.Saved);
            Assert.True(DocManager.Inst.ChangesSaved);
        }

        [Fact]
        public void AutosaveAfterAFailedSaveAsGoesNextToTheProject() {
            Save(savedPath);
            MakeEdit();
            Save(unwritablePath);

            DocManager.Inst.AutoSave();

            string autosavePath = Path.Combine(directory, "song-autosave.ustx");
            Assert.True(File.Exists(autosavePath));
            Assert.Equal(autosavePath, Preferences.Default.RecoveryPath);
            // A failed save keeps the recovery file; one that works clears it.
            Save(unwritablePath);
            Assert.Equal(autosavePath, Preferences.Default.RecoveryPath);
            Save("");
            Assert.Equal(string.Empty, Preferences.Default.RecoveryPath);
        }
    }
}
