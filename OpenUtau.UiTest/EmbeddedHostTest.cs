using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenUtau.App.Controls;
using OpenUtau.App.Embedding;
using OpenUtau.App.ViewModels;
using OpenUtau.Core;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.UiTest {
    /// <summary>
    /// What the embedded host does in the piano roll. The host's native entry points need an app that hosts .NET,
    /// so these call the managed code behind them on a piano roll in a window of its own.
    /// </summary>
    public class EmbeddedHostTest {
        // Track 0 has a short part at the start and a long part after a gap. Track 1 has a part of its own.
        // WithPianoRoll validates it once OpenUtau has started, so that phonemes are asked for.
        static UProject Project(out UVoicePart first, out UVoicePart second) {
            var project = Core.Format.Ustx.Create();
            project.tracks.Add(new UTrack(project) { TrackNo = 1 });
            first = AddPart(project, 0, 0, 4);
            second = AddPart(project, 0, 19200, 160);
            AddPart(project, 1, 1920, 8);
            project.AfterLoad();
            // Without a singer, OpenUtau phonemizes to nothing. Missing, a singer's lyrics are sung as they are.
            foreach (var track in project.tracks) {
                track.Singer = USinger.CreateMissing("test");
            }
            return project;
        }

        // A singer with no voicebank that has an oto for every lyric, so its notes become phrases to render.
        sealed class TestSinger : USinger {
            public TestSinger() {
                found = true;
                loaded = true;
            }
            public override string Id => "test";
            public override string Name => "test";
            public override USingerType SingerType => USingerType.Classic;
            public override IList<USubbank> Subbanks { get; } = new List<USubbank>();
            public override bool TryGetOto(string phoneme, out UOto oto) {
                oto = new UOto();
                return true;
            }
            // The phonemizer sings each lyric as it is.
            public override bool TryGetMappedOto(string phoneme, int tone, string color, out UOto oto) {
                oto = default!;
                return false;
            }
        }

        // Renders each phrase as silence, at once.
        sealed class SilentRenderer : IRenderer {
            public const string Name = "SILENT";
            public USingerType SingerType => USingerType.Classic;
            public bool SupportsExpression(UExpressionDescriptor descriptor) => false;
            public RenderResult Layout(RenderPhrase phrase) => new() {
                leadingMs = phrase.leadingMs,
                positionMs = phrase.positionMs,
                estimatedLengthMs = phrase.leadingMs + phrase.durationMs,
            };
            public Task<RenderResult> Render(RenderPhrase phrase, Progress progress, int trackNo,
                    CancellationTokenSource cancellation, bool isPreRender = false,
                    RenderPhraseEvents? renderEvents = null) {
                var result = Layout(phrase);
                result.samples = new float[(int)(result.estimatedLengthMs * 44100 / 1000)];
                return Task.FromResult(result);
            }
            public RenderPitchResult LoadRenderedPitch(RenderPhrase phrase) => new();
            public UExpressionDescriptor[] GetSuggestedExpressions(USinger singer, URenderSettings renderSettings) =>
                Array.Empty<UExpressionDescriptor>();
            public override string ToString() => Name;
        }

        // Gives each track the singer and renderer above, so OpenUtau renders the project's parts.
        static UProject Renderable(UProject project) {
            foreach (var track in project.tracks) {
                track.Singer = new TestSinger();
                track.RendererSettings = new URenderSettings {
                    renderer = SilentRenderer.Name,
                    Renderer = new SilentRenderer(),
                };
            }
            return project;
        }

        internal static UVoicePart AddPart(UProject project, int trackNo, int position, int count) {
            var part = new UVoicePart { trackNo = trackNo, position = position, name = $"part at {position}" };
            for (int i = 0; i < count; ++i) {
                var note = project.CreateNote(60 + i % 5, i * 480, 480);
                note.lyric = "a";
                part.notes.Add(note);
            }
            part.Duration = count * 480;
            project.parts.Add(part);
            return part;
        }

        // Loads the project, opens a part and shows the piano roll at about the size SVCoverEditor gives it.
        // beforeShow runs while the piano roll has no size yet, as when a call queued before OpenUtau was ready runs.
        // Afterwards the project is replaced by an empty one with no changes, as other tests expect.
        static void WithPianoRoll(UProject project, UVoicePart part, Action<PianoRoll> test,
                Action<PianoRoll>? beforeShow = null) => HeadlessUi.Run(() => {
            MainWindowTest.InitCore();
            // Embedded OpenUtau has no main window, which would validate edits on its frames.
            DocManager.Inst.RequestFrame = null;
            project.ValidateFull();
            DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
            // As in OpenUtau, the view model is built off the UI thread.
            var vm = System.Threading.Tasks.Task.Run(() => new PianoRollViewModel()).Result;
            var pianoRoll = new PianoRoll(vm);
            pianoRoll.InitializePianoRollWindowAsync();
            pianoRoll.ViewModel.PlaybackViewModel = new PlaybackViewModel();
            var window = new Window { Width = 1200, Height = 700, Content = pianoRoll };
            try {
                DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, project, part.position));
                HeadlessUi.Flush();
                beforeShow?.Invoke(pianoRoll);
                window.Show();
                HeadlessUi.Flush();
                test(pianoRoll);
                // Phonemes arriving after the project is replaced would belong to no project.
                WaitForPhonemes(project);
            } finally {
                window.Close();
                DocManager.Inst.RemoveSubscriber(pianoRoll);
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(Core.Format.Ustx.Create()));
                HeadlessUi.Flush();
            }
        });

        static void Seek(int tick) {
            DocManager.Inst.ExecuteCmd(new SeekPlayPosTickNotification(tick));
            HeadlessUi.Flush();
        }

        internal static void WaitForPhonemes(UProject project) {
            var clock = Stopwatch.StartNew();
            while (project.parts.OfType<UVoicePart>().Any(p => !p.PhonemesUpToDate)) {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "Phonemes didn't arrive");
                Thread.Sleep(10);
                HeadlessUi.Flush();
            }
        }

        static JsonElement Snapshot(UProject project) {
            using var json = JsonDocument.Parse(EmbeddedMix.Snapshot(project, false).json);
            return json.RootElement.Clone();
        }

        // Reads snapshots, as the application gets them while OpenUtau renders, until no part is rendering or failed.
        static JsonElement Rendered(UProject project) {
            var clock = Stopwatch.StartNew();
            while (true) {
                var snapshot = Snapshot(project);
                if (snapshot.GetProperty("rendering").GetInt32() == 0
                    && snapshot.GetProperty("failed").GetInt32() == 0) {
                    return snapshot;
                }
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"Parts didn't render: {snapshot}");
                Thread.Sleep(10);
                HeadlessUi.Flush();
            }
        }

        // Starts a render pass once the phonemes are in, as an edit does, and waits for every part to render.
        static JsonElement RenderAll(UProject project) {
            WaitForPhonemes(project);
            DocManager.Inst.ExecuteCmd(new PreRenderNotification());
            return Rendered(project);
        }

        static void ChangeLyric(UVoicePart part, string lyric) {
            DocManager.Inst.StartUndoGroup("test");
            DocManager.Inst.ExecuteCmd(new ChangeNoteLyricCommand(part, part.notes.ElementAt(0), lyric));
            DocManager.Inst.EndUndoGroup();
            DocManager.Inst.FlushValidation();
        }

        // A part whose audio is current has no "outdated" at all.
        static void AssertCurrent(JsonElement part) => Assert.False(part.TryGetProperty("outdated", out _));

        static void AssertShown(NotesViewModel notesVm, IEnumerable<UNote> notes) {
            foreach (var note in notes) {
                Assert.InRange(note.position, notesVm.TickOffset, notesVm.TickOffset + notesVm.ViewportTicks);
                Assert.InRange(note.End, notesVm.TickOffset, notesVm.TickOffset + notesVm.ViewportTicks);
                double row = notesVm.TrackCount - 1 - note.tone;
                Assert.InRange(row, notesVm.TrackOffset, notesVm.TrackOffset + notesVm.ViewportTracks - 1);
            }
        }

        [Fact]
        public void PlayheadScrollsThroughALongPart() {
            var project = Project(out _, out var part);
            WithPianoRoll(project, part, pianoRoll => {
                var notesVm = pianoRoll.ViewModel.NotesViewModel;
                var offsets = new List<double>();
                for (int tick = part.position; tick < part.position + 40 * 480; tick += 120) {
                    Seek(tick);
                    offsets.Add(notesVm.TickOffset);
                }
                Assert.Equal(0, offsets.First());
                Assert.True(offsets.Last() > notesVm.ViewportTicks, $"Scrolled only to {offsets.Last()}");
            });
        }

        [Fact]
        public void PianoRollCantScrollAPartShorterThanTheView() {
            // Why the duet's piano roll stayed put as it played: each part fits the view, so there is nowhere to scroll.
            var project = Project(out var part, out _);
            WithPianoRoll(project, part, pianoRoll => {
                var notesVm = pianoRoll.ViewModel.NotesViewModel;
                Assert.Equal(0, notesVm.HScrollBarMax);
                for (int tick = 0; tick < part.End; tick += 240) {
                    Seek(tick);
                    Assert.Equal(0, notesVm.TickOffset);
                }
            });
        }

        [Fact]
        public void PlayheadOpensTheTracksNextPart() {
            var project = Project(out var first, out var second);
            WithPianoRoll(project, first, pianoRoll => {
                var notesVm = pianoRoll.ViewModel.NotesViewModel;
                Seek(first.position + 960);
                Assert.Same(first, notesVm.Part);
                // In the gap the part stays open, and the next opens once the play head reaches it.
                Seek(first.End + 960);
                Assert.Same(first, notesVm.Part);
                Seek(second.position + 960);
                Assert.Same(second, notesVm.Part);
            });
        }

        [Fact]
        public void ShowNotesOpensTheirPartSelectsAndScrollsToThem() {
            var project = Project(out var first, out var second);
            WithPianoRoll(project, first, pianoRoll => {
                var notesVm = pianoRoll.ViewModel.NotesViewModel;
                var wanted = second.notes.Skip(100).Take(3).ToList();
                var ticks = wanted.Select(n => second.position + n.position).ToList();
                // A tick naming a note of the first part, after the first found, is left out.
                ticks.Add(first.position + first.notes.Last().position);

                Assert.True(EmbeddedHost.ShowNotesIn(pianoRoll, 0, ticks, true));
                HeadlessUi.Flush();
                Assert.Same(second, notesVm.Part);
                Assert.Equal(wanted, notesVm.Selection.ToList());
                AssertShown(notesVm, wanted);
                // The first note starts about a quarter of the way across.
                Assert.Equal(0.25, (wanted[0].position - notesVm.TickOffset) / notesVm.ViewportTicks, 2);

                // Without selecting, the selection stays as it was.
                var others = second.notes.Skip(10).Take(1).ToList();
                Assert.True(EmbeddedHost.ShowNotesIn(pianoRoll, 0, new[] { second.position + others[0].position }, false));
                HeadlessUi.Flush();
                Assert.Equal(wanted, notesVm.Selection.ToList());
                AssertShown(notesVm, others);

                Assert.False(EmbeddedHost.ShowNotesIn(pianoRoll, 0, new[] { 7 }, true));
                Assert.False(EmbeddedHost.ShowNotesIn(pianoRoll, 5, ticks, true));
                Assert.Same(second, notesVm.Part);
                Assert.Equal(wanted, notesVm.Selection.ToList());
            });
        }

        [Fact]
        public void ShowNotesBeforeThePianoRollHasASizeScrollsOnceItHasOne() {
            var project = Project(out var first, out var second);
            var wanted = second.notes.Skip(100).Take(3).ToList();
            WithPianoRoll(project, first, beforeShow: pianoRoll => {
                Assert.Equal(0, pianoRoll.ViewModel.NotesViewModel.Bounds.Width);
                Assert.True(EmbeddedHost.ShowNotesIn(pianoRoll, 0, wanted.Select(n => second.position + n.position).ToList(), true));
            }, test: pianoRoll => {
                var notesVm = pianoRoll.ViewModel.NotesViewModel;
                Assert.Same(second, notesVm.Part);
                Assert.Equal(wanted, notesVm.Selection.ToList());
                AssertShown(notesVm, wanted);
                Assert.Equal(0.25, (wanted[0].position - notesVm.TickOffset) / notesVm.ViewportTicks, 2);
            });
        }

        [Fact]
        public void ShowTrackAtBeforeThePianoRollHasASizeCentresOnceItHasOne() {
            var project = Project(out var first, out var second);
            WithPianoRoll(project, first, beforeShow: pianoRoll => {
                Assert.True(EmbeddedHost.ShowPartIn(pianoRoll, 0, second.position + 80 * 480));
                // Asked again before it has a size, as the part is now open, it still centres.
                Assert.True(EmbeddedHost.ShowPartIn(pianoRoll, 0, second.position + 80 * 480));
            }, test: pianoRoll => {
                var notesVm = pianoRoll.ViewModel.NotesViewModel;
                Assert.Same(second, notesVm.Part);
                Assert.Equal(80 * 480, notesVm.TickOffset + notesVm.ViewportTicks / 2, 0);
            });
        }

        [Fact]
        public void ShowTrackAtThePartOpenAtItsStartCentresOnALaterTick() {
            // As when the app first shows a singer of the project it opened, whose part opened at its start.
            var project = Project(out _, out var second);
            WithPianoRoll(project, second, pianoRoll => {
                var notesVm = pianoRoll.ViewModel.NotesViewModel;
                Assert.Equal(0, notesVm.TickOffset);
                Assert.True(EmbeddedHost.ShowPartIn(pianoRoll, 0, second.position + 80 * 480));
                HeadlessUi.Flush();
                Assert.Same(second, notesVm.Part);
                Assert.Equal(80 * 480, notesVm.TickOffset + notesVm.ViewportTicks / 2, 0);
            });
        }

        [Fact]
        public void ShowTrackAtOpensThePartUnderTheTick() {
            var project = Project(out var first, out var second);
            var other = project.parts.OfType<UVoicePart>().Single(p => p.trackNo == 1);
            WithPianoRoll(project, first, pianoRoll => {
                var notesVm = pianoRoll.ViewModel.NotesViewModel;
                int tick = second.position + 80 * 480;
                Assert.True(EmbeddedHost.ShowPartIn(pianoRoll, 0, tick));
                HeadlessUi.Flush();
                Assert.Same(second, notesVm.Part);
                // The part opens centred on the tick.
                Assert.Equal(80 * 480, notesVm.TickOffset + notesVm.ViewportTicks / 2, 0);

                // A part already open stays where it is scrolled while the tick is in view, and otherwise centres on it,
                // so the view needn't page across to the play head as the song plays.
                notesVm.TickOffset = 1000;
                Assert.True(EmbeddedHost.ShowPartIn(pianoRoll, 0, second.position + 1000 + 480));
                Assert.Equal(1000, notesVm.TickOffset);
                Assert.True(EmbeddedHost.ShowPartIn(pianoRoll, 0, second.position + 120 * 480));
                HeadlessUi.Flush();
                Assert.Equal(120 * 480, notesVm.TickOffset + notesVm.ViewportTicks / 2, 0);

                // In a gap the part before it opens, and another track's part is found the same way.
                Assert.True(EmbeddedHost.ShowPartIn(pianoRoll, 0, first.End + 960));
                Assert.Same(first, notesVm.Part);
                Assert.True(EmbeddedHost.ShowPartIn(pianoRoll, 1, 0));
                Assert.Same(other, notesVm.Part);
                Assert.False(EmbeddedHost.ShowPartIn(pianoRoll, 5, 0));
                Assert.Same(other, notesVm.Part);
            });
        }

        [Fact]
        public void ShowTrackAtAnOpenPartMovesOnlyForATickPastTheViewsEdges() {
            var project = Project(out _, out var second);
            WithPianoRoll(project, second, pianoRoll => {
                var notesVm = pianoRoll.ViewModel.NotesViewModel;
                void Show(int at, double from, bool centres) {
                    notesVm.TickOffset = from;
                    Assert.True(EmbeddedHost.ShowPartIn(pianoRoll, 0, second.position + at));
                    HeadlessUi.Flush();
                    if (centres) {
                        Assert.Equal(at, notesVm.TickOffset + notesVm.ViewportTicks / 2, 0);
                    } else {
                        Assert.Equal(from, notesVm.TickOffset);
                    }
                }
                // The first tick shown is in view, and the one before it isn't.
                Show(20 * 480, 20 * 480, centres: false);
                Show(20 * 480 - 1, 20 * 480, centres: true);
                // The view ends where the next tick starts, which is out of view.
                double before = 40 * 480 - notesVm.ViewportTicks;
                Show(40 * 480 - 1, before, centres: false);
                Show(40 * 480, before, centres: true);
            });
        }

        [Fact]
        public void PhonemesArePendingUntilOpenUtauHasThemAndTheAppIsTold() {
            var project = Project(out var first, out _);
            var told = new List<int>();
            WithPianoRoll(project, first, pianoRoll => {
                try {
                    WaitForPhonemes(project);
                    EmbeddedHost.Listen(told.Add);
                    var note = first.notes.ElementAt(0);
                    DocManager.Inst.StartUndoGroup("test");
                    DocManager.Inst.ExecuteCmd(new ChangeNoteLyricCommand(first, note, "la"));
                    using (var json = JsonDocument.Parse(EmbeddedNotes.PhonemesJson(project))) {
                        Assert.True(json.RootElement.GetProperty("pending").GetBoolean());
                        Assert.True(json.RootElement.GetProperty("tracks")[0].GetProperty("pending").GetBoolean());
                    }
                    DocManager.Inst.ExecuteCmd(new ChangeNoteLyricCommand(first, first.notes.ElementAt(1), "li"));
                    DocManager.Inst.EndUndoGroup();
                    // Never told within a call into OpenUtau.
                    Assert.Empty(told);

                    WaitForPhonemes(project);
                    Assert.NotEmpty(told);
                    Assert.All(told, change => Assert.Equal(0, change));
                    using (var json = JsonDocument.Parse(EmbeddedNotes.PhonemesJson(project))) {
                        Assert.False(json.RootElement.GetProperty("pending").GetBoolean());
                        var notes = json.RootElement.GetProperty("tracks")[0].GetProperty("notes");
                        var la = notes.EnumerateArray().First();
                        Assert.Equal("la", la.GetProperty("lyric").GetString());
                        Assert.NotEqual(0, la.GetProperty("phonemes").GetArrayLength());
                    }
                } finally {
                    EmbeddedHost.Listen(null);
                }
            });
        }

        [Fact]
        public void TheMixSnapshotHasTheTracksTheUstxAndThePartsStillRendering() {
            var project = Project(out var first, out _);
            WithPianoRoll(project, first, pianoRoll => {
                DocManager.Inst.StartUndoGroup("test");
                DocManager.Inst.ExecuteCmd(new ChangeNoteLyricCommand(first, first.notes.ElementAt(0), "la"));
                DocManager.Inst.EndUndoGroup();
                DocManager.Inst.FlushValidation();

                var (text, rendering) = EmbeddedMix.Snapshot(project, true);

                using var json = JsonDocument.Parse(text);
                var root = json.RootElement;
                Assert.Equal(2, root.GetProperty("tracks").GetArrayLength());
                Assert.Equal("test", root.GetProperty("tracks")[0].GetProperty("singer").GetString());
                // The edited part waits for its phonemes, and a part is listed only once it has audio.
                Assert.InRange(rendering, 1, 3);
                Assert.Equal(rendering, root.GetProperty("rendering").GetInt32());
                Assert.Equal(3 - rendering, root.GetProperty("parts").GetArrayLength());
                // The project has never been saved, and its USTX is written all the same.
                Assert.False(root.GetProperty("saved").GetBoolean());
                Assert.Contains("lyric: la", root.GetProperty("ustx").GetString());
                Assert.Equal(string.Empty, project.FilePath);

                using var unchanged = JsonDocument.Parse(EmbeddedMix.Snapshot(project, false).json);
                Assert.False(unchanged.RootElement.TryGetProperty("ustx", out _));
                Assert.Equal(2, EmbeddedMix.ReadAudio("1", out _));
            });
        }

        [Fact]
        public void PartsAFailedRenderLeftAreFailedUntilTheNextPass() {
            var project = Renderable(Project(out var first, out _));
            WithPianoRoll(project, first, pianoRoll => {
                EmbeddedMix.Listen(null);
                var before = RenderAll(project).GetProperty("parts")[0].GetProperty("audioHash").GetString();
                int Count(string name) => Snapshot(project).GetProperty(name).GetInt32();
                var prefs = Core.Util.Preferences.Default;
                bool preRender = prefs.PreRender;
                // No pass starts for the edit, so its part waits as one a failed pass left would.
                prefs.PreRender = false;
                try {
                    ChangeLyric(first, "la");
                    int waiting = Count("rendering");
                    Assert.True(waiting > 0);

                    DocManager.Inst.ExecuteCmd(new ToastNotification("Pianoroll", "Failed to pre-render.", "errors.failed.prerender"));
                    HeadlessUi.Flush();

                    var failed = Snapshot(project);
                    Assert.Equal((0, waiting), (failed.GetProperty("rendering").GetInt32(), failed.GetProperty("failed").GetInt32()));
                    // Still read again while they wait, as a phrase build can land after the failure and tells nothing.
                    Assert.Equal(waiting, EmbeddedMix.Snapshot(project, false).waiting);
                    // The part the pass left keeps its last audio, marked outdated.
                    var left = failed.GetProperty("parts")[0];
                    Assert.Equal(before, left.GetProperty("audioHash").GetString());
                    Assert.True(left.GetProperty("outdated").GetBoolean());
                } finally {
                    prefs.PreRender = preRender;
                }

                DocManager.Inst.ExecuteCmd(new PreRenderNotification());
                HeadlessUi.Flush();

                Assert.Equal(0, Count("failed"));
                var rendered = Rendered(project).GetProperty("parts")[0];
                Assert.NotEqual(before, rendered.GetProperty("audioHash").GetString());
                AssertCurrent(rendered);
            });
        }

        [Fact]
        public void AnEditedPartIsOutdatedUntilItRendersAgain() {
            var project = Renderable(Project(out var first, out _));
            WithPianoRoll(project, first, pianoRoll => {
                EmbeddedMix.Listen(null);
                var parts = RenderAll(project).GetProperty("parts");
                Assert.Equal(3, parts.GetArrayLength());
                Assert.All(parts.EnumerateArray(), AssertCurrent);
                var before = parts[0].GetProperty("audioHash").GetString();

                ChangeLyric(first, "la");

                // Only the edited part is outdated, and it keeps its last audio until the new audio is in.
                parts = Snapshot(project).GetProperty("parts");
                Assert.Equal(3, parts.GetArrayLength());
                Assert.Equal(before, parts[0].GetProperty("audioHash").GetString());
                Assert.True(parts[0].GetProperty("outdated").GetBoolean());
                AssertCurrent(parts[1]);
                AssertCurrent(parts[2]);

                var rendered = Rendered(project).GetProperty("parts")[0];
                Assert.NotEqual(before, rendered.GetProperty("audioHash").GetString());
                AssertCurrent(rendered);
            });
        }

        [Fact]
        public void AnEditUndoneBeforeItRendersLeavesThePartCurrentWithTheSameAudio() {
            var project = Renderable(Project(out var first, out _));
            WithPianoRoll(project, first, pianoRoll => {
                EmbeddedMix.Listen(null);
                var before = RenderAll(project).GetProperty("parts")[0].GetProperty("audioHash").GetString();

                ChangeLyric(first, "la");
                var outdated = Snapshot(project).GetProperty("parts")[0];
                Assert.Equal(before, outdated.GetProperty("audioHash").GetString());
                Assert.True(outdated.GetProperty("outdated").GetBoolean());
                DocManager.Inst.Undo();

                // The part names the same audio as it did while outdated, and its entry still changes, so a snapshot
                // that differs from the last one sent tells the application the part is current again.
                var current = Rendered(project).GetProperty("parts")[0];
                Assert.Equal(before, current.GetProperty("audioHash").GetString());
                AssertCurrent(current);
                Assert.NotEqual(outdated.GetRawText(), current.GetRawText());
            });
        }

        // Notes each phonemizer result in a log, between the listener's calls.
        sealed class PhonemesArrive : ICmdSubscriber {
            public readonly List<string> log;
            public PhonemesArrive(List<string> log) {
                this.log = log;
            }
            public void OnNext(UCommand cmd, bool isUndo) {
                if (cmd is PhonemizedNotification) {
                    log.Add("phonemes");
                }
            }
        }

        [Fact]
        public void AppIsToldOnceOfABurstOfChanges() {
            var project = Project(out var first, out _);
            var log = new List<string>();
            var arrive = new PhonemesArrive(log);
            WithPianoRoll(project, first, pianoRoll => {
                try {
                    WaitForPhonemes(project);
                    EmbeddedHost.Listen(change => log.Add($"told {change}"));
                    DocManager.Inst.AddSubscriber(arrive);
                    DocManager.Inst.StartUndoGroup("test");
                    foreach (var note in first.notes) {
                        DocManager.Inst.ExecuteCmd(new ChangeNoteLyricCommand(first, note, "o"));
                    }
                    DocManager.Inst.EndUndoGroup();
                    Assert.Empty(log);
                    WaitForPhonemes(project);

                    // The burst is told once. Phonemes for it may arrive before or after, and are told again
                    // unless they arrive while a telling is waiting.
                    var told = Enumerable.Range(0, log.Count).Where(i => log[i].StartsWith("told")).ToList();
                    Assert.NotEmpty(told);
                    Assert.All(told, i => Assert.Equal("told 0", log[i]));
                    for (int k = 1; k < told.Count; ++k) {
                        Assert.Contains("phonemes", log.GetRange(told[k - 1] + 1, told[k] - told[k - 1] - 1));
                    }

                    EmbeddedHost.Listen(null);
                    log.Clear();
                    DocManager.Inst.StartUndoGroup("test");
                    DocManager.Inst.ExecuteCmd(new ChangeNoteLyricCommand(first, first.notes.ElementAt(0), "u"));
                    DocManager.Inst.EndUndoGroup();
                    WaitForPhonemes(project);
                    Assert.DoesNotContain(log, entry => entry.StartsWith("told"));
                } finally {
                    EmbeddedHost.Listen(null);
                    DocManager.Inst.RemoveSubscriber(arrive);
                }
            });
        }

        // Notes each part whose phonemes arrive though the open project doesn't have it.
        sealed class ForeignPhonemes : ICmdSubscriber {
            public readonly List<UPart> parts = new();
            public void OnNext(UCommand cmd, bool isUndo) {
                if (cmd is PhonemizedNotification phonemized && !DocManager.Inst.Project.parts.Contains(phonemized.part)) {
                    parts.Add(phonemized.part);
                }
            }
        }

        [Fact]
        public void ImportSendsTheOpenProjectNoPhonemes() {
            // Importers validate the project they read, and OpenUtau's phonemizer answers through the open project.
            // Answers for an import's parts would start a render and tell the app of changes it doesn't have.
            const string svp = "{\"version\":197,\"time\":{\"meter\":[{\"index\":0,\"numerator\":4,\"denominator\":4}]," +
                "\"tempo\":[{\"position\":0,\"bpm\":120.0}]},\"tracks\":[{\"name\":\"Lead\",\"mainGroup\":{" +
                "\"uuid\":\"lead\",\"name\":\"main\",\"notes\":[" +
                "{\"onset\":0,\"duration\":705600000,\"lyrics\":\"la\",\"pitch\":64}," +
                "{\"onset\":705600000,\"duration\":705600000,\"lyrics\":\"la\",\"pitch\":62}]}," +
                "\"mainRef\":{\"groupID\":\"lead\",\"blickOffset\":0,\"database\":{\"name\":\"Kevin\"}," +
                "\"voice\":{\"mouthOpening\":0}}}]}";
            var project = Project(out var first, out _);
            var foreign = new ForeignPhonemes();
            var folder = Path.Combine(Path.GetTempPath(), "OpenUtauEmbeddedImportTest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            WithPianoRoll(project, first, pianoRoll => {
                try {
                    WaitForPhonemes(project);
                    DocManager.Inst.AddSubscriber(foreign);
                    var source = Path.Combine(folder, "song.svp");
                    File.WriteAllText(source, svp);
                    EmbeddedHost.Import(source, Path.Combine(folder, "song.ustx"));
                    Assert.True(File.Exists(Path.Combine(folder, "song.ustx")));

                    // The phonemizer takes requests in order, and answers a batch of them at once, so answers for the
                    // import arrive by the time an edit made after it has its phonemes, or straight after.
                    ChangeLyric(first, "o");
                    WaitForPhonemes(project);
                    for (int i = 0; i < 20; ++i) {
                        Thread.Sleep(10);
                        HeadlessUi.Flush();
                    }
                    Assert.Empty(foreign.parts);
                } finally {
                    DocManager.Inst.RemoveSubscriber(foreign);
                    Directory.Delete(folder, recursive: true);
                }
            });
        }

        [Fact]
        public void SaveIsFalseWhenOpenUtauCouldntWriteTheFile() => HeadlessUi.Run(() => {
            MainWindowTest.InitCore();
            var directory = Path.Combine(Path.GetTempPath(), "OpenUtauEmbeddedSaveTest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "song.ustx");
            try {
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(Core.Format.Ustx.Create()));
                DocManager.Inst.ExecuteCmd(new SaveProjectNotification(path));
                var project = DocManager.Inst.Project;
                DocManager.Inst.StartUndoGroup();
                DocManager.Inst.ExecuteCmd(new RenameTrackCommand(project, project.tracks[0], "Renamed"));
                DocManager.Inst.EndUndoGroup();
                File.SetAttributes(path, FileAttributes.ReadOnly);
                HeadlessUi.Errors.Clear();

                // SaveProject, which the app calls, reports this to the app as an error.
                Assert.False(EmbeddedHost.Save());
                Assert.False(DocManager.Inst.ChangesSaved);
                Assert.Equal(path, project.FilePath);
                // The project has still been saved to its file before.
                Assert.True(project.Saved);
                Assert.Contains(HeadlessUi.Errors.Snapshot(), error => error.Contains(path));

                File.SetAttributes(path, FileAttributes.Normal);
                Assert.True(EmbeddedHost.Save());
                Assert.True(DocManager.Inst.ChangesSaved);
            } finally {
                HeadlessUi.Errors.Clear();
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(Core.Format.Ustx.Create()));
                if (File.Exists(path)) {
                    File.SetAttributes(path, FileAttributes.Normal);
                }
                Directory.Delete(directory, recursive: true);
            }
        });

        [Fact]
        public void DialogOwnerShowsOnlyWhileItOwnsAWindow() => HeadlessUi.Run(() => {
            var owner = EmbeddedHost.DialogOwner();
            Assert.True(owner.IsVisible);
            // Asked for but used for no window, as when a dialog decides not to open.
            HeadlessUi.Flush();
            Assert.False(owner.IsVisible);

            for (int i = 0; i < 2; ++i) {
                Assert.Same(owner, EmbeddedHost.DialogOwner());
                var dialog = new Window { Width = 300, Height = 200 };
                _ = dialog.ShowDialog(owner);
                HeadlessUi.Flush();
                Assert.True(owner.IsVisible);
                Assert.True(dialog.IsVisible);
                dialog.Close();
                HeadlessUi.Flush();
                Assert.False(owner.IsVisible);
            }

            // A window that isn't modal, as the expressions dialog is, keeps it until it closes too.
            var expressions = new Window { Width = 300, Height = 200 };
            expressions.Show(EmbeddedHost.DialogOwner());
            HeadlessUi.Flush();
            Assert.True(owner.IsVisible);
            expressions.Close();
            HeadlessUi.Flush();
            Assert.False(owner.IsVisible);
        });

        // A track whose notes were written for two voice colors, and whose singer now has three others, as after a
        // singer change in the track bar. The standalone OpenUtau's main window asks how to map them; the embedded
        // host has no main window, so it asks itself, over the piano roll.
        [Fact]
        public void ASingerWithOtherVoiceColorsAsksHowToMapThem() => HeadlessUi.Run(() => {
            MainWindowTest.InitCore();
            var project = Core.Format.Ustx.Create();
            AddPart(project, 0, 0, 2);
            project.AfterLoad();
            DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
            var track = project.tracks[0];
            track.Singer = new TestSinger();
            track.VoiceColorNames = new[] { "soft", "strong" };
            track.VoiceColorExp = new UExpressionDescriptor("voice color", Core.Format.Ustx.CLR, false, new[] { "airy", "bright", "dark" });
            var remap = new EmbeddedHost.RemapVoiceColors();
            DocManager.Inst.AddSubscriber(remap);
            var owner = EmbeddedHost.DialogOwner();
            try {
                DocManager.Inst.ExecuteCmd(new VoiceColorRemappingNotification(track.TrackNo, true));
                HeadlessUi.Flush();
                var dialog = Assert.Single(owner.OwnedWindows.OfType<App.Views.VoiceColorMappingDialog>());
                var mapping = Assert.IsType<VoiceColorMappingViewModel>(dialog.DataContext);
                // The first color is the default one.
                Assert.Equal(new[] { "(Default)", "strong" }, mapping.ColorMappings.Select(m => m.Name));
                dialog.Close();
                HeadlessUi.Flush();
                Assert.False(owner.IsVisible);
            } finally {
                DocManager.Inst.RemoveSubscriber(remap);
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(Core.Format.Ustx.Create()));
                HeadlessUi.Flush();
            }
        });

        // The application can replace the project while a remap dialog is open, since DialogOwner blocks nothing. The
        // dialog closes, and accepting it late, as its OK button would have, leaves the new project as it was.
        [Fact]
        public void ReplacingTheProjectDismissesAPendingRemap() => HeadlessUi.Run(() => {
            MainWindowTest.InitCore();
            var project = Core.Format.Ustx.Create();
            var part = AddPart(project, 0, 0, 2);
            project.AfterLoad();
            var track = project.tracks[0];
            track.Singer = new TestSinger();
            project.ValidateFull();
            DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
            WaitForPhonemes(project);
            // The first phoneme sings the old second color, "strong".
            var phoneme = part.phonemes.First();
            phoneme.SetExpression(project, track, Core.Format.Ustx.CLR, 1);
            track.VoiceColorNames = new[] { "soft", "strong" };
            track.VoiceColorExp = new UExpressionDescriptor("voice color", Core.Format.Ustx.CLR, false, new[] { "airy", "bright", "dark" });
            var remap = new EmbeddedHost.RemapVoiceColors();
            DocManager.Inst.AddSubscriber(remap);
            var owner = EmbeddedHost.DialogOwner();
            try {
                DocManager.Inst.ExecuteCmd(new VoiceColorRemappingNotification(track.TrackNo, true));
                HeadlessUi.Flush();
                var dialog = Assert.Single(owner.OwnedWindows.OfType<App.Views.VoiceColorMappingDialog>());
                // Map "strong" onto "dark".
                Assert.IsType<VoiceColorMappingViewModel>(dialog.DataContext).ColorMappings[1].SelectedIndex = 2;

                var replacement = Core.Format.Ustx.Create();
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(replacement));
                HeadlessUi.Flush();
                Assert.False(dialog.IsVisible);
                Assert.Empty(owner.OwnedWindows.OfType<App.Views.VoiceColorMappingDialog>());

                dialog.onFinish?.Invoke();
                HeadlessUi.Flush();
                Assert.Same(replacement, DocManager.Inst.Project);
                Assert.True(DocManager.Inst.ChangesSaved);
                Assert.Equal(1, phoneme.GetExpression(project, track, Core.Format.Ustx.CLR).Item1);
            } finally {
                DocManager.Inst.RemoveSubscriber(remap);
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(Core.Format.Ustx.Create()));
                HeadlessUi.Flush();
            }
        });

        // A DiffSinger singer with a default subbank and a "breathy" one.
        sealed class ColoredSinger : USinger {
            public ColoredSinger() {
                found = true;
                loaded = true;
            }
            public override string Id => "colored";
            public override string Name => "colored";
            public override USingerType SingerType => USingerType.DiffSinger;
            public override IList<USubbank> Subbanks { get; } = new List<USubbank> {
                new USubbank(new Classic.Subbank { Color = "" }),
                new USubbank(new Classic.Subbank { Color = "breathy" }),
            };
        }

        // Opening a project checks every track. When the application replaces the project while that check waits on
        // a dialog, the check stops: it must not go on to ask about the replacement's imported vocal modes with the
        // old track's singer, and add its color curves to the replacement.
        [Fact]
        public void ReplacingTheProjectStopsTheRemapOfEveryTrack() => HeadlessUi.Run(() => {
            MainWindowTest.InitCore();
            var project = Core.Format.Ustx.Create();
            AddPart(project, 0, 0, 2);
            project.AfterLoad();
            DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
            var track = project.tracks[0];
            track.Singer = new ColoredSinger();
            track.VoiceColorNames = new[] { "soft", "strong" };
            track.VoiceColorExp = new UExpressionDescriptor("voice color", Core.Format.Ustx.CLR, false, new[] { "", "breathy" });
            var remap = new EmbeddedHost.RemapVoiceColors();
            DocManager.Inst.AddSubscriber(remap);
            var owner = EmbeddedHost.DialogOwner();
            try {
                DocManager.Inst.ExecuteCmd(new VoiceColorRemappingNotification(-1, true));
                HeadlessUi.Flush();
                Assert.Single(owner.OwnedWindows.OfType<App.Views.VoiceColorMappingDialog>());

                // The replacement has an imported "Whisper" vocal mode on its first track.
                var replacement = Core.Format.Ustx.Create();
                var part = AddPart(replacement, 0, 0, 2);
                var whisper = new UExpressionDescriptor("Whisper", "whp", 0, 100, 0) { type = UExpressionType.Curve };
                replacement.RegisterExpression(whisper);
                var curve = new UCurve(whisper);
                curve.xs.AddRange(new[] { 0, 480 });
                curve.ys.AddRange(new[] { 0, 50 });
                part.curves.Add(curve);
                replacement.AfterLoad();
                // As a project opened from its file is.
                replacement.Saved = true;
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(replacement));
                HeadlessUi.Flush();

                Assert.Empty(owner.OwnedWindows.OfType<App.Views.VoiceColorMappingDialog>());
                Assert.DoesNotContain(part.curves, c => c.abbr.StartsWith("cl"));
                Assert.True(DocManager.Inst.ChangesSaved);
            } finally {
                DocManager.Inst.RemoveSubscriber(remap);
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(Core.Format.Ustx.Create()));
                HeadlessUi.Flush();
            }
        });

        [Fact]
        public void PumpRunsTheJobsWaitingWhenItIsCalled() => HeadlessUi.Run(() => {
            HeadlessUi.Flush();
            var ran = new List<DispatcherPriority>();
            foreach (var priority in new[] { DispatcherPriority.Background, DispatcherPriority.Input, DispatcherPriority.Normal }) {
                Dispatcher.UIThread.Post(() => ran.Add(priority), priority);
            }

            EmbeddedHost.RunPending();

            Assert.Equal(new[] { DispatcherPriority.Normal, DispatcherPriority.Input, DispatcherPriority.Background }, ran);
        });

        // The headless dispatcher can't ask whether input waits, so it stops running a stream of Input jobs after
        // 50 ms, as X11 input arriving would, and Pump's own job still waits behind the stream.
        [Fact]
        public void PumpReturnsDuringAStreamOfInput() => HeadlessUi.Run(() => {
            HeadlessUi.Flush();
            bool streaming = true;
            int handled = 0;
            void Next() {
                ++handled;
                if (streaming) {
                    Dispatcher.UIThread.Post(Next, DispatcherPriority.Input);
                }
            }
            Dispatcher.UIThread.Post(Next, DispatcherPriority.Input);
            var watch = Stopwatch.StartNew();

            EmbeddedHost.RunPending();
            streaming = false;
            HeadlessUi.Flush();

            Assert.InRange(watch.ElapsedMilliseconds, 40, 2000);
            Assert.True(handled > 1);
        });
    }
}
