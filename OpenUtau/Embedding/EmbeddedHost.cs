using System;
using System.Collections.Generic;
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
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.X11;
using OpenUtau.App.Controls;
using OpenUtau.App.ViewModels;
using OpenUtau.Audio;
using OpenUtau.Classic;
using OpenUtau.Core;
using OpenUtau.Core.DawIntegration;
using OpenUtau.Core.Ustx;
using ReactiveUI;
using ReactiveUI.Primitives;
using Serilog;

namespace OpenUtau.App.Embedding {
    /// <summary>
    /// Lets a native macOS or Linux application show OpenUtau's piano roll inside its own window.
    /// The application starts the .NET runtime with OpenUtau.dll as the app, then calls these
    /// entry points on its main thread. OpenUtau opens no windows of its own, except dialogs.
    /// </summary>
    public static class EmbeddedHost {
        static EmbeddableControlRoot? root;
        // On Linux, the XEmbed client window that shows root.
        static XEmbedPlug? plug;
        static PianoRoll? pianoRoll;
        // The piano roll with the track bar above it.
        static DockPanel? editor;
        static Window? dialogOwner;
        static bool ready;
        internal static bool Ready => ready;
        static string? pendingProject;
        // The last call to show a track or notes before OpenUtau was ready, run once it is.
        static Action? pendingView;
        // A scroll waiting for the piano roll to have a size, replaced by the next.
        static IDisposable? pendingScroll;
        static string? pendingServer;
        // Start's search for tools, singers and plugins.
        static Task loading = Task.CompletedTask;
        // Made when first shown, so an import before Start, as in tests, doesn't tie Avalonia to its thread.
        static TextBlock? statusText;
        static TextBlock status => statusText ??= new() {
            Text = "Starting OpenUtau…",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void TransportFunction(int command, double ms);
        static TransportFunction? transport;
        const int playOrPause = 0, pause = 1, seek = 2;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void TextFunction(IntPtr context, IntPtr utf8, int length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void ListenerFunction(int change);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void MixFunction(IntPtr utf8, int length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void SamplesFunction(IntPtr context, IntPtr samples, int frames);
        static Action<int>? listener;
        static TellChanges? tellChanges;
        // One bit for each change waiting to be told, so changes in quick succession are told once.
        static int untold;
        const int phonemesChanged = 0;

        /// <summary>OpenUtau renders the project ahead of playback when it loads and after each edit. A part
        /// whose phonemes arrive after that render has started is left out of it, and OpenUtau's own playback
        /// would render it on demand. The application plays the song instead, so OpenUtau renders again once
        /// the phonemes arrive.</summary>
        sealed class RenderWhenPhonemized : ICmdSubscriber {
            public void OnNext(UCommand cmd, bool isUndo) {
                if (cmd is PhonemizedNotification phonemized) {
                    DocManager.Inst.ExecuteCmd(new PreRenderNotification(phonemized.part));
                }
            }
        }

        /// <summary>Tells the application when what <see cref="ReadPhonemes"/> reports may have changed. Edits
        /// that change what OpenUtau sings, such as tempo or expressions, phonemize again, so they are told when
        /// the phonemes arrive. Vibrato changes nothing reported.</summary>
        sealed class TellChanges : ICmdSubscriber {
            public void OnNext(UCommand cmd, bool isUndo) {
                if (cmd is PhonemizedNotification || cmd is LoadProjectNotification
                    || cmd is TrackCommand || cmd is PartCommand
                    || cmd is NoteCommand && cmd is not VibratoCommand) {
                    Tell(phonemesChanged);
                }
            }
        }

        static void Tell(int change) {
            if (listener == null) {
                return;
            }
            // Only a change with nothing else waiting posts, and the post tells all that wait by then.
            if (Interlocked.Or(ref untold, 1 << change) != 0) {
                return;
            }
            Dispatcher.UIThread.Post(() => {
                int changes = Interlocked.Exchange(ref untold, 0);
                for (int i = 0; i < 31; ++i) {
                    if ((changes & (1 << i)) != 0) {
                        listener?.Invoke(i);
                    }
                }
            });
        }

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
        /// uses OpenUtau's usual data folder. On Linux, Avalonia runs on X11, and its events wait for
        /// <see cref="Pump"/>.</summary>
        [UnmanagedCallersOnly]
        public static int Start(IntPtr dataPath, IntPtr cachePath) => Guard("start", () => {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            PathManager.DataPathOverride = Utf8(dataPath);
            PathManager.CachePathOverride = Utf8(cachePath);
            Program.InitLogging();
            Log.Information($"Embedded OpenUtau. Data path = {PathManager.Inst.DataPath}");
            App.Embedded = true;
            var builder = Program.BuildAvaloniaApp();
            if (OperatingSystem.IsLinux()) {
                // XEmbed, which puts the piano roll in the application's window, is X11's. Wayland has none.
                builder.UseX11();
            }
            builder
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
            loading = Task.Run(() => {
                ToolsManager.Inst.Initialize();
                SingerManager.Inst.Initialize();
                DocManager.Inst.Initialize(mainThread, mainScheduler);
                DocManager.Inst.PostOnUIThread = action => Dispatcher.UIThread.Post(action);
                // The hosting application plays the sound.
                PlaybackManager.Inst.AudioOutput = new DummyAudioOutput();
            });
            loading.ContinueWith(t => {
                var failure = t.Exception?.InnerException;
                PianoRollViewModel? viewModel = null;
                if (failure == null) {
                    try {
                        // An Avalonia object belongs to the thread that makes it, and the view model makes some, such
                        // as ThemeManager's brushes when it first uses ThemeManager, so it is made on the main thread.
                        viewModel = new PianoRollViewModel();
                    } catch (Exception e) {
                        failure = e;
                    }
                }
                if (viewModel == null) {
                    Log.Error(failure, "Embedded OpenUtau failed to start.");
                    status.Text = $"OpenUtau couldn't start. {failure?.Message}";
                    return;
                }
                pianoRoll = new PianoRoll(viewModel);
                pianoRoll.InitializePianoRollWindowAsync();
                pianoRoll.ViewModel.PlaybackViewModel = new PlaybackViewModel();
                DocManager.Inst.AddSubscriber(new RenderWhenPhonemized());
                var bar = new TrackBar(ShowTrack);
                DockPanel.SetDock(bar, Dock.Top);
                editor = new DockPanel { Children = { bar, pianoRoll } };
                // A menu or tooltip that is a window of its own opens away from what opened it, as far right and down
                // as the piano roll is from the corner of the application's window. A tooltip takes the setting from
                // the control that has it, not from its popup.
                editor.Styles.Add(new Style(x => x.OfType<Popup>()) {
                    Setters = { new Setter(Popup.ShouldUseOverlayLayerProperty, true) },
                });
                editor.Styles.Add(new Style(x => x.Is<Control>()) {
                    Setters = { new Setter(ToolTip.ShouldUseOverlayLayerProperty, true) },
                });
                ready = true;
                Log.Information("Embedded OpenUtau ready.");
                EmbeddedMix.Schedule();
                if (root != null) {
                    root.Content = editor;
                    pianoRoll.Focus();
                }
                if (pendingProject != null) {
                    LoadProject(pendingProject);
                }
                var view = pendingView;
                pendingView = null;
                view?.Invoke();
                if (pendingServer != null) {
                    ConnectTo(pendingServer);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, mainScheduler);
        });

        /// <summary>Returns the view showing the piano roll, the same view each time, for the application to add to
        /// its own window. On macOS it is a retained NSView for the application's view hierarchy. On Linux it is the
        /// X11 window of an XEmbed client, for an XEmbed embedder in the application's window to take.</summary>
        [UnmanagedCallersOnly]
        public static IntPtr CreateView() {
            IntPtr view = IntPtr.Zero;
            Guard("create view", () => {
                if (root == null) {
                    root = CreateRoot();
                    // As the detached piano roll window does, give the piano roll the keyboard.
                    root.GotFocus += (_, e) => {
                        if (e.Source == root && ready) {
                            pianoRoll?.Focus();
                        }
                    };
                }
                root.Content = ready ? editor : status;
                root.Prepare();
                root.StartRendering();
                if (ready) {
                    pianoRoll?.Focus();
                }
                view = plug != null
                    ? plug.Handle
                    : ((IMacOSTopLevelPlatformHandle)root.TryGetPlatformHandle()!).GetNSViewRetained();
            });
            return view;
        }

        static EmbeddableControlRoot CreateRoot() {
            if (!OperatingSystem.IsLinux()) {
                var embedded = new EmbeddableControlRoot();
                if (embedded.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle) {
                    throw new InvalidOperationException("No NSView for the embedded top level");
                }
                return embedded;
            }
            plug = XEmbedPlug.Create();
            // The plug keeps its top level to itself, and content set on it joins that top level straight away.
            plug.Content = status;
            return TopLevel.GetTopLevel(status) as EmbeddableControlRoot
                ?? throw new InvalidOperationException("No top level for the XEmbed client");
        }

        /// <summary>Runs what OpenUtau has waiting on Linux, where OpenUtau's X11 events and jobs wait for the
        /// application's run loop: the X11 events that have arrived, then the jobs and timers due. The application
        /// calls it on the main thread every few milliseconds, as from a timer. It returns once nothing above
        /// background work waits, or after 50 ms while X11 input keeps arriving. Jobs still run while no input waits,
        /// so a stream of them holds the application's main thread until it ends, as it would hold OpenUtau's own
        /// loop. On macOS the application's run loop runs OpenUtau's, so the application never needs to call it.</summary>
        [UnmanagedCallersOnly]
        public static int Pump() => Guard("pump", RunPending);

        /// <summary>The managed part of <see cref="Pump"/>.</summary>
        public static void RunPending() {
            var frame = new DispatcherFrame();
            // Background jobs wait for the X11 events that have arrived, so this runs once those and the jobs above
            // it have.
            Dispatcher.UIThread.Post(() => frame.Continue = false, DispatcherPriority.Background);
            using var deadline = DispatcherTimer.RunOnce(() => frame.Continue = false,
                TimeSpan.FromMilliseconds(50), DispatcherPriority.Send);
            Dispatcher.UIThread.PushFrame(frame);
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
            pendingView = null;
            if (ready) {
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(Core.Format.Ustx.Create()));
                DocManager.Inst.Recovered = false;
                Log.Information("Embedded OpenUtau closed its project");
            }
        });

        /// <summary>Reads a project in another format, such as an SVP, through OpenUtau's own importer, and saves it
        /// as a new USTX without opening it, so the open project stays as it was.</summary>
        [UnmanagedCallersOnly]
        public static int ImportProject(IntPtr source, IntPtr destination) => Guard("import project", () =>
            Import(Utf8(source) ?? throw new ArgumentNullException(nameof(source)),
                Utf8(destination) ?? throw new ArgumentNullException(nameof(destination))));

        /// <summary>The managed part of <see cref="ImportProject"/>. It refuses a destination that exists, and the
        /// USTX appears whole or not at all.</summary>
        public static void Import(string source, string destination) {
            if (File.Exists(destination)) {
                throw new IOException($"\"{destination}\" already exists");
            }
            // The importer gives tracks installed singers, which Start may still be searching for when the
            // application imports. If the search failed, the tracks start without singers.
            Task.WhenAny(loading).Wait();
            // The phonemizer would answer the importer's requests through the open project.
            var project = DocManager.Inst.ReadWithoutPhonemizing(() => Core.Format.Formats.ReadProject(new[] { source }))
                ?? throw new InvalidDataException($"No project in \"{source}\"");
            project.ustxVersion = Core.Format.Ustx.kUstxVersion;
            project.FilePath = destination;
            project.BeforeSave();
            var partial = destination + ".importing";
            try {
                File.WriteAllText(partial, Yaml.DefaultSerializer.Serialize(project), Encoding.UTF8);
                File.Move(partial, destination);
            } finally {
                project.AfterSave();
                File.Delete(partial);
            }
            Log.Information($"Embedded OpenUtau imported {source} as {destination}");
        }

        /// <summary>Shows a track's first voice part in the piano roll.</summary>
        [UnmanagedCallersOnly]
        public static int OpenTrack(int trackNo) => Guard("open track", () => {
            if (ready) {
                ShowTrack(trackNo);
            } else {
                pendingView = () => ShowTrack(trackNo);
            }
        });

        /// <summary>Shows a track's notes in the piano roll, named by their start ticks from the project's start.
        /// It opens the part holding the first note found, scrolls as the application's SynthV script does, so the
        /// notes start a quarter of the way across, or near the left if they are wide, with their pitches centred, and
        /// selects them in place of the selection if <paramref name="select"/> isn't 0. Notes in other parts are left
        /// out. The ticks are read before this returns. Until the piano roll has a size, the scroll waits for one.
        /// Returns 0 once shown, or queued until OpenUtau is ready, 1 if it failed, and 2 if no tick names a note of
        /// the track.</summary>
        [UnmanagedCallersOnly]
        public static int ShowNotes(int trackNo, IntPtr ticks, int count, int select) {
            int result = 0;
            int failed = Guard("show notes", () => {
                if (count < 0 || count > 0 && ticks == IntPtr.Zero) {
                    throw new ArgumentException($"No ticks for {count} notes");
                }
                var starts = new int[count];
                if (count > 0) {
                    Marshal.Copy(ticks, starts, 0, count);
                }
                if (ready) {
                    result = ShowNotesNow(trackNo, starts, select != 0) ? 0 : 2;
                } else {
                    pendingView = () => ShowNotesNow(trackNo, starts, select != 0);
                }
            });
            return failed != 0 ? failed : result;
        }

        /// <summary>Shows a track's part under a tick from the project's start, or else the part ending last before
        /// it, or else its first part. Where parts overlap, it is the part OpenUtau's playback would open. A part
        /// already open stays where it is scrolled while the tick is in view, and otherwise centres on it; another
        /// opens centred on the tick, or as near it as the part reaches, once the piano roll has a size. Returns 0 once
        /// shown, or queued until OpenUtau is ready, 1 if it failed, and 2 if the track has no voice part.</summary>
        [UnmanagedCallersOnly]
        public static int ShowTrackAt(int trackNo, int tick) {
            int result = 0;
            int failed = Guard("show track at a tick", () => {
                if (ready) {
                    result = ShowPartAt(trackNo, tick) ? 0 : 2;
                } else {
                    pendingView = () => ShowPartAt(trackNo, tick);
                }
            });
            return failed != 0 ? failed : result;
        }

        /// <summary>Gives <paramref name="function"/> what OpenUtau sings, as UTF-8 JSON, with
        /// <paramref name="context"/> passed back to it, and returns 0. The JSON is described at
        /// <see cref="EmbeddedNotes.PhonemesJson"/>. The function is called once, on the main thread, before this
        /// returns. Its text belongs to OpenUtau and is valid only during the call, so copy what you keep. Its length
        /// is in bytes and leaves out the NUL that ends it. Until OpenUtau is ready the text is
        /// <c>{"pending":true,"tracks":[]}</c>. Returns 1, without calling the function, if it is null, or if OpenUtau
        /// is ready and this is called on another thread.</summary>
        [UnmanagedCallersOnly]
        public static int ReadPhonemes(IntPtr function, IntPtr context) => Guard("read phonemes", () => {
            if (function == IntPtr.Zero) {
                throw new ArgumentNullException(nameof(function));
            }
            if (ready && !Dispatcher.UIThread.CheckAccess()) {
                throw new InvalidOperationException("Phonemes are read on the main thread");
            }
            var receive = Marshal.GetDelegateForFunctionPointer<TextFunction>(function);
            if (ready) {
                // An edit OpenUtau validates later would leave its part looking up to date until then.
                DocManager.Inst.FlushValidation();
            }
            var json = EmbeddedNotes.PhonemesJson(ready ? DocManager.Inst.Project : null);
            var text = new byte[json.Length + 1];
            json.CopyTo(text, 0);
            var pinned = GCHandle.Alloc(text, GCHandleType.Pinned);
            try {
                receive(context, pinned.AddrOfPinnedObject(), json.Length);
            } finally {
                pinned.Free();
            }
        });

        /// <summary>Takes the function OpenUtau tells of changes to, or null to stop. It is called on the main thread
        /// with 0 when what <see cref="ReadPhonemes"/> reports may have changed: when phonemes arrive, a project
        /// opens, or tracks, parts or notes change. It runs from the main thread's run loop, never within a call into
        /// OpenUtau, and a burst of changes is told once. Other numbers will tell of other changes, so ignore those
        /// you don't know.</summary>
        [UnmanagedCallersOnly]
        public static int SetListener(IntPtr function) => Guard("set listener", () => {
            var native = function == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<ListenerFunction>(function);
            Listen(native == null ? null : change => native(change));
        });

        /// <summary>The managed side of <see cref="SetListener"/>.</summary>
        public static void Listen(Action<int>? function) {
            listener = function;
            if (tellChanges == null) {
                tellChanges = new TellChanges();
                DocManager.Inst.AddSubscriber(tellChanges);
            }
        }

        /// <summary>
        /// Takes the function the project's snapshots go to in place of the DAW Integration, or null to stop. It is
        /// called on the main thread with UTF-8 JSON, valid only during the call, once OpenUtau is ready and again
        /// after each change, with nothing changed sent twice. The JSON has the project's <c>name</c> and whether it
        /// is <c>saved</c>, its <c>tracks</c> as the DAW Integration's <c>updateTracks</c> gives them, and its
        /// <c>parts</c> as <c>updatePartLayout</c> does. <c>ustx</c>, the project as OpenUtau saves it, is there after
        /// a change to the project and left out otherwise. A part renders again after each edit to it. Until it has,
        /// it is listed with its last audio and <c>outdated</c> true, or left out if it has never rendered, and
        /// <c>rendering</c> counts it. If OpenUtau's render pass failed, <c>failed</c> counts the parts it left in
        /// place of <c>rendering</c> until another pass starts, as an edit, Undo or Redo starts one. The application's
        /// playback doesn't. A part whose audio is current has no <c>outdated</c>, and a part loses it once it has
        /// rendered, even with the same audio as before, so the snapshot still changes. A part with no notes to sing
        /// is left out. <see cref="ReadPartAudio"/> gives each part's audio by its hash.
        /// </summary>
        [UnmanagedCallersOnly]
        public static int SetMix(IntPtr function) => Guard("set mix", () => {
            var native = function == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<MixFunction>(function);
            EmbeddedMix.Listen(native == null ? null : json => {
                var text = Encoding.UTF8.GetBytes(json);
                var pinned = GCHandle.Alloc(text, GCHandleType.Pinned);
                try {
                    native(pinned.AddrOfPinnedObject(), text.Length);
                } finally {
                    pinned.Free();
                }
            });
        });

        /// <summary>Gives <paramref name="function"/> the audio of a part the last snapshot from <see cref="SetMix"/>
        /// names by its hash, with <paramref name="context"/> passed back to it. The audio is the part's window as
        /// <c>getAudio</c> gives it: stereo float samples, interleaved, at 44.1 kHz. The function is called once,
        /// before this returns, with the number of sample frames, and the samples are valid only during the call.
        /// Safe on any thread. Returns 0 once given, 2 if no part in the last snapshot has the hash or OpenUtau has since
        /// dropped some of its audio, and 1 if it failed.</summary>
        [UnmanagedCallersOnly]
        public static int ReadPartAudio(IntPtr hash, IntPtr function, IntPtr context) {
            int result = 0;
            int failed = Guard("read part audio", () => {
                var name = Utf8(hash) ?? throw new ArgumentNullException(nameof(hash));
                if (function == IntPtr.Zero) {
                    throw new ArgumentNullException(nameof(function));
                }
                result = EmbeddedMix.ReadAudio(name, out var samples);
                if (result != 0) {
                    return;
                }
                var receive = Marshal.GetDelegateForFunctionPointer<SamplesFunction>(function);
                var pinned = GCHandle.Alloc(samples, GCHandleType.Pinned);
                try {
                    receive(context, pinned.AddrOfPinnedObject(), samples.Length / DawAudio.Channels);
                } finally {
                    pinned.Free();
                }
            });
            return failed != 0 ? failed : result;
        }

        /// <summary>Moves OpenUtau's playhead to the application's play head, in milliseconds from the project's start,
        /// as the DAW Integration's <c>playhead</c> does. Main thread.</summary>
        [UnmanagedCallersOnly]
        public static int SetPlayhead(double ms) => Guard("set playhead", () => {
            if (!ready) {
                return;
            }
            int tick = Math.Max(0, DocManager.Inst.Project.timeAxis.MsPosToTickPos(ms));
            // A parked play head reported again after rounding leaves OpenUtau's where it is.
            if (Math.Abs(tick - DocManager.Inst.playPosTick) >= DawManager.PlayheadEpsilonTicks) {
                DocManager.Inst.ExecuteCmd(new SeekPlayPosTickNotification(tick));
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
            var project = DocManager.Inst.Project;
            var part = project.parts.OfType<UVoicePart>().Where(p => p.trackNo == trackNo).OrderBy(p => p.position).FirstOrDefault();
            if (part == null || pianoRoll == null) {
                return;
            }
            DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, project, part.position));
            pianoRoll.AttachExpressions();
        }

        static bool ShowPartAt(int trackNo, int tick) => pianoRoll != null && ShowPartIn(pianoRoll, trackNo, tick);

        static bool ShowNotesNow(int trackNo, int[] ticks, bool select) => pianoRoll != null && ShowNotesIn(pianoRoll, trackNo, ticks, select);

        /// <summary>The piano roll part of <see cref="ShowTrackAt"/>. False if the track has no voice part.</summary>
        public static bool ShowPartIn(PianoRoll pianoRoll, int trackNo, int tick) {
            var project = DocManager.Inst.Project;
            var part = EmbeddedNotes.PartAt(project, trackNo, tick);
            if (part == null) {
                return false;
            }
            var notesVm = pianoRoll.ViewModel.NotesViewModel;
            int at = Math.Clamp(tick, part.position, part.End) - part.position;
            bool opened = notesVm.Part != part;
            // Without a size the view's offset means nothing yet, so the tick centres whatever it shows.
            bool centre = opened || notesVm.Bounds.Width <= 0 || notesVm.Bounds.Height <= 0;
            if (opened) {
                DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, project, part.position + at));
                pianoRoll.AttachExpressions();
            }
            // Loading centres the tick only if the piano roll already has a size. A part already open moves only when the
            // tick is out of view, since playback would otherwise page the view across to it.
            WhenSized(notesVm, part, () => {
                if (centre || at < notesVm.TickOffset || at >= notesVm.TickOffset + notesVm.ViewportTicks) {
                    notesVm.TickOffset = Math.Clamp(at - notesVm.ViewportTicks / 2, 0, notesVm.HScrollBarMax);
                }
            });
            return true;
        }

        /// <summary>The piano roll part of <see cref="ShowNotes"/>. False if no tick names a note of the track.</summary>
        public static bool ShowNotesIn(PianoRoll pianoRoll, int trackNo, IReadOnlyList<int> ticks, bool select) {
            var project = DocManager.Inst.Project;
            var found = EmbeddedNotes.FindNotes(project, trackNo, ticks);
            if (found == null) {
                return false;
            }
            var (part, notes) = found.Value;
            var notesVm = pianoRoll.ViewModel.NotesViewModel;
            if (notesVm.Part != part) {
                DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, project, part.position + notes[0].position));
                pianoRoll.AttachExpressions();
            }
            if (select) {
                notesVm.Selection.Select(notes);
                MessageBus.Current.SendMessage(new NotesSelectionEvent(notesVm.Selection));
            }
            WhenSized(notesVm, part, () => {
                var (tickOffset, trackOffset) = EmbeddedNotes.Placement(notes,
                    notesVm.ViewportTicks, notesVm.ViewportTracks, notesVm.HScrollBarMax, notesVm.VScrollBarMax);
                notesVm.TickOffset = tickOffset;
                notesVm.TrackOffset = trackOffset;
            });
            return true;
        }

        /// <summary>Scrolls the piano roll now if it has a size, otherwise once it has one, as when a call made
        /// before OpenUtau was ready runs before the piano roll is laid out. A scroll still waiting is dropped, and
        /// so is this one if another part opens first.</summary>
        static void WhenSized(NotesViewModel notesVm, UVoicePart part, Action scroll) {
            pendingScroll?.Dispose();
            pendingScroll = null;
            if (notesVm.Bounds.Width > 0 && notesVm.Bounds.Height > 0) {
                scroll();
                return;
            }
            IDisposable? waiting = null;
            waiting = notesVm.WhenAnyValue(x => x.Bounds).Subscribe(bounds => {
                // The first value, given as this subscribes, is the size it doesn't have yet.
                if (bounds.Width <= 0 || bounds.Height <= 0) {
                    return;
                }
                waiting?.Dispose();
                pendingScroll = null;
                if (notesVm.Part == part) {
                    scroll();
                }
            });
            pendingScroll = waiting;
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
            if (string.IsNullOrEmpty(DocManager.Inst.Project.FilePath)) {
                return false;
            }
            var save = new SaveProjectNotification("");
            DocManager.Inst.ExecuteCmd(save);
            return save.Saved;
        }

        /// <summary>Dialogs need an owner window that is showing, and the embedded piano roll has none, so a tiny
        /// transparent window at the piano roll's centre stands in, and dialogs centred on their owner open over the
        /// piano roll. It shows only while it owns a window. It hides once they have all closed, or straight after the
        /// caller's work if that opened none.</summary>
        public static Window DialogOwner() {
            dialogOwner ??= new Window {
                Width = 1,
                Height = 1,
                WindowDecorations = WindowDecorations.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Background = Brushes.Transparent,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            };
            if (!dialogOwner.IsVisible) {
                if (root != null && root.Bounds.Width > 0) {
                    dialogOwner.Position = root.PointToScreen(new Point(root.Bounds.Width / 2, root.Bounds.Height / 2));
                }
                dialogOwner.Show();
            }
            Dispatcher.UIThread.Post(HideDialogOwnerIfUnused, DispatcherPriority.Background);
            return dialogOwner;
        }

        static void HideDialogOwnerIfUnused() {
            if (dialogOwner == null || !dialogOwner.IsVisible) {
                return;
            }
            if (dialogOwner.OwnedWindows.Count == 0) {
                dialogOwner.Hide();
                return;
            }
            foreach (var window in dialogOwner.OwnedWindows) {
                window.Closed -= OnOwnedWindowClosed;
                window.Closed += OnOwnedWindowClosed;
            }
        }

        static void OnOwnedWindowClosed(object? sender, EventArgs e) {
            Dispatcher.UIThread.Post(HideDialogOwnerIfUnused, DispatcherPriority.Background);
        }
    }
}
