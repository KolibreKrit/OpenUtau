using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenUtau.App.Controls;
using OpenUtau.App.Embedding;
using OpenUtau.App.ViewModels;
using OpenUtau.Core;
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

        static UVoicePart AddPart(UProject project, int trackNo, int position, int count) {
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
        // Afterwards the project is replaced by an empty one with no changes, as other tests expect.
        static void WithPianoRoll(UProject project, UVoicePart part, Action<PianoRoll> test) => HeadlessUi.Run(() => {
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

        static void WaitForPhonemes(UProject project) {
            var clock = Stopwatch.StartNew();
            while (project.parts.OfType<UVoicePart>().Any(p => !p.PhonemesUpToDate)) {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "Phonemes didn't arrive");
                Thread.Sleep(10);
                HeadlessUi.Flush();
            }
        }

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

                // A part already open stays where it is scrolled.
                notesVm.TickOffset = 1000;
                Assert.True(EmbeddedHost.ShowPartIn(pianoRoll, 0, second.position + 10));
                Assert.Equal(1000, notesVm.TickOffset);

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
        public void AppIsToldOnceOfABurstOfChanges() {
            var project = Project(out var first, out _);
            var told = new List<int>();
            WithPianoRoll(project, first, pianoRoll => {
                try {
                    WaitForPhonemes(project);
                    EmbeddedHost.Listen(told.Add);
                    DocManager.Inst.StartUndoGroup("test");
                    foreach (var note in first.notes) {
                        DocManager.Inst.ExecuteCmd(new ChangeNoteLyricCommand(first, note, "o"));
                    }
                    DocManager.Inst.EndUndoGroup();
                    // Only the jobs waiting now, so phonemes arriving after can't add to the count.
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(new[] { 0 }, told);

                    EmbeddedHost.Listen(null);
                    WaitForPhonemes(project);
                    told.Clear();
                    DocManager.Inst.StartUndoGroup("test");
                    DocManager.Inst.ExecuteCmd(new ChangeNoteLyricCommand(first, first.notes.ElementAt(0), "u"));
                    DocManager.Inst.EndUndoGroup();
                    WaitForPhonemes(project);
                    Assert.Empty(told);
                } finally {
                    EmbeddedHost.Listen(null);
                }
            });
        }

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
    }
}
