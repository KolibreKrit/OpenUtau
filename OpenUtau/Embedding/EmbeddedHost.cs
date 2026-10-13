using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Embedding;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using OpenUtau.App.Controls;
using OpenUtau.App.ViewModels;
using OpenUtau.Audio;
using OpenUtau.Classic;
using OpenUtau.Core;
using OpenUtau.Core.DawIntegration;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.App.Embedding {
    /// <summary>
    /// Lets a native macOS application show OpenUtau's piano roll inside its own window.
    /// The application starts the .NET runtime with OpenUtau.dll as the app, then calls these
    /// entry points on its main thread. OpenUtau opens no windows of its own, except dialogs.
    /// </summary>
    public static class EmbeddedHost {
        static EmbeddableControlRoot? root;
        static PianoRoll? pianoRoll;
        static Window? dialogOwner;
        static bool ready;
        static string? pendingProject;
        static int pendingTrack = -1;
        static string? pendingServer;
        static readonly TextBlock status = new() {
            Text = "Starting OpenUtau…",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void TransportFunction(int command, double ms);
        static TransportFunction? transport;
        const int playOrPause = 0, pause = 1, seek = 2;

        static string? Utf8(IntPtr text) => text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text);

        static int Guard(string what, Action action) {
            try {
                action();
                return 0;
            } catch (Exception e) {
                Log.Error(e, $"Embedded OpenUtau: {what} failed");
                Console.Error.WriteLine($"Embedded OpenUtau: {what} failed: {e}");
                return 1;
            }
        }

        /// <summary>Starts Avalonia and OpenUtau without any OpenUtau window. A null data path
        /// uses OpenUtau's usual data folder.</summary>
        [UnmanagedCallersOnly]
        public static int Start(IntPtr dataPath, IntPtr cachePath) => Guard("start", () => {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            PathManager.DataPathOverride = Utf8(dataPath);
            PathManager.CachePathOverride = Utf8(cachePath);
            Program.InitLogging();
            Log.Information($"Embedded OpenUtau. Data path = {PathManager.Inst.DataPath}");
            App.Embedded = true;
            Program.BuildAvaloniaApp()
                .With(new MacOSPlatformOptions {
                    DisableAvaloniaAppDelegate = true,
                    DisableNativeMenus = true,
                    DisableDefaultApplicationMenuItems = true,
                    DisableSetProcessName = true,
                })
                .SetupWithLifetime(new ClassicDesktopStyleApplicationLifetime {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                });
            AvaloniaSynchronizationContext.InstallIfNeeded();
            var mainThread = Thread.CurrentThread;
            var mainScheduler = TaskScheduler.FromCurrentSynchronizationContext();
            Task.Run(() => {
                ToolsManager.Inst.Initialize();
                SingerManager.Inst.Initialize();
                DocManager.Inst.Initialize(mainThread, mainScheduler);
                DocManager.Inst.PostOnUIThread = action => Dispatcher.UIThread.Post(action);
                // The hosting application plays the sound.
                PlaybackManager.Inst.AudioOutput = new DummyAudioOutput();
                return new PianoRollViewModel();
            }).ContinueWith(t => {
                if (t.IsFaulted) {
                    Log.Error(t.Exception?.Flatten(), "Embedded OpenUtau failed to start.");
                    status.Text = $"OpenUtau couldn't start. {t.Exception?.InnerException?.Message}";
                    return;
                }
                pianoRoll = new PianoRoll(t.Result);
                pianoRoll.InitializePianoRollWindowAsync();
                pianoRoll.ViewModel.PlaybackViewModel = new PlaybackViewModel();
                ready = true;
                Log.Information("Embedded OpenUtau ready.");
                if (root != null) {
                    root.Content = pianoRoll;
                    pianoRoll.Focus();
                }
                if (pendingProject != null) {
                    LoadProject(pendingProject);
                }
                if (pendingTrack >= 0) {
                    ShowTrack(pendingTrack);
                }
                if (pendingServer != null) {
                    ConnectTo(pendingServer);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, mainScheduler);
        });

        /// <summary>Returns a retained NSView showing the piano roll, for the application to add
        /// to its own view hierarchy.</summary>
        [UnmanagedCallersOnly]
        public static IntPtr CreateView() {
            IntPtr view = IntPtr.Zero;
            Guard("create view", () => {
                if (root == null) {
                    root = new EmbeddableControlRoot();
                    // As the detached piano roll window does, give the piano roll the keyboard.
                    root.GotFocus += (_, e) => {
                        if (e.Source == root && ready) {
                            pianoRoll?.Focus();
                        }
                    };
                }
                if (root.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle) {
                    throw new InvalidOperationException("No NSView for the embedded top level");
                }
                root.Content = ready ? pianoRoll : status;
                root.Prepare();
                root.StartRendering();
                if (ready) {
                    pianoRoll?.Focus();
                }
                view = handle.GetNSViewRetained();
            });
            return view;
        }

        /// <summary>Opens a USTX project in place of the current one.</summary>
        [UnmanagedCallersOnly]
        public static int OpenProject(IntPtr path) => Guard("open project", () => {
            var file = Utf8(path) ?? throw new ArgumentNullException(nameof(path));
            if (ready) {
                LoadProject(file);
            } else {
                pendingProject = file;
            }
        });

        /// <summary>Puts an empty project in place of the current one, discarding unsaved changes, so
        /// nothing of the last project shows or plays.</summary>
        [UnmanagedCallersOnly]
        public static int CloseProject() => Guard("close project", () => {
            pendingProject = null;
            pendingTrack = -1;
            if (ready) {
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(Core.Format.Ustx.Create()));
                DocManager.Inst.Recovered = false;
                Log.Information("Embedded OpenUtau closed its project");
            }
        });

        /// <summary>Shows a track's first voice part in the piano roll.</summary>
        [UnmanagedCallersOnly]
        public static int OpenTrack(int trackNo) => Guard("open track", () => {
            if (ready) {
                ShowTrack(trackNo);
            } else {
                pendingTrack = trackNo;
            }
        });

        /// <summary>Connects OpenUtau's DAW Integration to the server a discovery file names.</summary>
        [UnmanagedCallersOnly]
        public static int Connect(IntPtr discoveryFile) => Guard("connect", () => {
            var file = Utf8(discoveryFile) ?? throw new ArgumentNullException(nameof(discoveryFile));
            if (ready) {
                ConnectTo(file);
            } else {
                pendingServer = file;
            }
        });

        /// <summary>Takes the function OpenUtau's play, pause and play head moves go to in place of its own
        /// playback, as the application plays the song. It is called on the main thread with 0 to play or pause,
        /// 1 to pause, and 2 to move the play head to a time in milliseconds from the project's start.</summary>
        [UnmanagedCallersOnly]
        public static int SetTransport(IntPtr function) => Guard("set transport", () => {
            transport = function == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<TransportFunction>(function);
        });

        /// <summary>True if the application took the request.</summary>
        internal static bool PlayOrPause(int tick) {
            if (transport == null) {
                return false;
            }
            if (tick >= 0) {
                transport(seek, DocManager.Inst.Project.timeAxis.TickPosToMsPos(tick));
            }
            transport(playOrPause, 0);
            return true;
        }

        internal static bool Pause() {
            transport?.Invoke(pause, 0);
            return transport != null;
        }

        internal static void Seek(int tick) {
            transport?.Invoke(seek, DocManager.Inst.Project.timeAxis.TickPosToMsPos(tick));
        }

        static void LoadProject(string file) {
            pendingProject = null;
            Core.Format.Formats.LoadProject(new[] { file });
            DocManager.Inst.ExecuteCmd(new VoiceColorRemappingNotification(-1, true));
            Log.Information($"Embedded OpenUtau opened {file}");
        }

        static void ShowTrack(int trackNo) {
            pendingTrack = -1;
            var project = DocManager.Inst.Project;
            var part = project.parts.OfType<UVoicePart>().Where(p => p.trackNo == trackNo).OrderBy(p => p.position).FirstOrDefault();
            if (part == null || pianoRoll == null) {
                return;
            }
            DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, project, part.position));
            pianoRoll.AttachExpressions();
        }

        static void ConnectTo(string file) {
            pendingServer = null;
            var info = JsonSerializer.Deserialize<DawServerInfo>(File.ReadAllText(file))
                ?? throw new InvalidDataException($"No server in {file}");
            var server = new DawServer(file, info);
            DawManager.Inst.ConnectAsync(server).ContinueWith(t => {
                if (t.IsFaulted) {
                    Log.Error(t.Exception?.Flatten(), $"Embedded OpenUtau couldn't connect to {server}");
                } else {
                    Log.Information($"Embedded OpenUtau connected to {server}");
                }
            });
        }

        /// <summary>Saves the project to its file if it has unsaved changes. Returns 1 if the project has no file
        /// or OpenUtau couldn't write it.</summary>
        [UnmanagedCallersOnly]
        public static int SaveProject() => Guard("save", () => {
            if (ready && !DocManager.Inst.ChangesSaved && !Save()) {
                throw new IOException($"Couldn't save the project to \"{DocManager.Inst.Project.FilePath}\"");
            }
        });

        /// <summary>1 if the project has changes not yet saved to its file, otherwise 0.</summary>
        [UnmanagedCallersOnly]
        public static int HasChanges() => ready && !DocManager.Inst.ChangesSaved ? 1 : 0;

        /// <summary>Saves the project to its file, as OpenUtau's Save does. False if the project has no file or
        /// OpenUtau couldn't write it, which OpenUtau itself only reports in a message.</summary>
        public static bool Save() {
            var project = DocManager.Inst.Project;
            if (string.IsNullOrEmpty(project.FilePath)) {
                return false;
            }
            // OpenUtau marks the project saved once it has written the file. Until then its changes stay unsaved.
            project.Saved = false;
            DocManager.Inst.ExecuteCmd(new SaveProjectNotification(""));
            return project.Saved;
        }

        /// <summary>Dialogs need an owner window, and the embedded piano roll has none, so a
        /// tiny window stands in.</summary>
        public static Window DialogOwner() {
            if (dialogOwner == null) {
                dialogOwner = new Window {
                    Width = 1,
                    Height = 1,
                    WindowDecorations = WindowDecorations.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Background = Brushes.Transparent,
                    TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                };
                dialogOwner.Show();
            }
            return dialogOwner;
        }
    }
}
