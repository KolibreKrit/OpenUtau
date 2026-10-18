using System.Linq;
using System.Text;
using System.Text.Json;
using OpenUtau.App.Embedding;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Test.App {
    public class EmbeddedNotesTest {
        static UNote Note(int position, int duration, int tone, string lyric) {
            var note = UNote.Create();
            note.position = position;
            note.duration = duration;
            note.tone = tone;
            note.lyric = lyric;
            return note;
        }

        static UPhoneme Phoneme(UNote parent, int position, string raw, string sung = null) =>
            new UPhoneme { Parent = parent, rawPosition = position, position = position, rawPhoneme = raw, phoneme = sung ?? raw };

        static UVoicePart Part(UProject project, int trackNo, int position, int duration, params UNote[] notes) {
            var part = new UVoicePart { trackNo = trackNo, position = position, duration = duration };
            foreach (var note in notes) {
                part.notes.Add(note);
            }
            project.parts.Add(part);
            return part;
        }

        static UProject Project(params string[] tracks) {
            var project = new UProject();
            project.tracks.Clear();
            foreach (var name in tracks) {
                project.tracks.Add(new UTrack(name));
            }
            return project;
        }

        [Fact]
        public void PartAtTakesThePartUnderTheTickOrTheLastBefore() {
            var project = Project("Lead", "Harmony");
            var first = Part(project, 0, 960, 960);
            var second = Part(project, 0, 3840, 1920);
            var other = Part(project, 1, 0, 9600);

            Assert.Same(second, EmbeddedNotes.PartAt(project, 0, 4000));
            Assert.Same(first, EmbeddedNotes.PartAt(project, 0, 960));
            // Between parts, and past the last, the part that ended last before the tick.
            Assert.Same(first, EmbeddedNotes.PartAt(project, 0, 2500));
            Assert.Same(second, EmbeddedNotes.PartAt(project, 0, 20000));
            // Before every part, the first.
            Assert.Same(first, EmbeddedNotes.PartAt(project, 0, 100));
            Assert.Same(other, EmbeddedNotes.PartAt(project, 1, 4000));
            Assert.Null(EmbeddedNotes.PartAt(project, 2, 0));

            // Where parts overlap, the first in the project, as OpenUtau's playback picks, even if it starts earlier.
            var overlapping = Part(project, 0, 4800, 1920);
            Assert.Same(second, EmbeddedNotes.PartAt(project, 0, 5000));
            Assert.Same(overlapping, EmbeddedNotes.PartAt(project, 0, 6000));
        }

        [Fact]
        public void FindNotesShowsThePartOfTheFirstNoteFound() {
            var project = Project("Lead", "Harmony");
            var a = Note(0, 480, 60, "a");
            var b = Note(480, 480, 62, "b");
            var c = Note(480, 480, 64, "c");
            var d = Note(960, 480, 65, "d");
            Part(project, 0, 0, 1920, a, b);
            var second = Part(project, 0, 3840, 1920, c, d);
            Part(project, 1, 3840, 1920, Note(480, 480, 60, "other"));

            // 999 names no note; 4800 is d and comes first; 480 is b, in the other part, so it's left out.
            var found = EmbeddedNotes.FindNotes(project, 0, new[] { 999, 4800, 480, 4320 });
            Assert.NotNull(found);
            Assert.Same(second, found.Value.part);
            Assert.Equal(new[] { c, d }, found.Value.notes);

            Assert.Null(EmbeddedNotes.FindNotes(project, 0, new[] { 1, 2 }));
            Assert.Null(EmbeddedNotes.FindNotes(project, 0, new int[0]));
            Assert.Null(EmbeddedNotes.FindNotes(project, 5, new[] { 0 }));
        }

        // OpenUtau's top tone, ViewConstants.MaxTone, which tests can't see.
        const int maxTone = 12 * 11;

        [Fact]
        public void PlacementPutsNotesAQuarterAcrossWithPitchesCentred() {
            var notes = new[] { Note(1920, 240, 60, "a"), Note(2160, 240, 64, "b") };
            var (tickOffset, trackOffset) = EmbeddedNotes.Placement(notes, 1920, 20, 100000, 1000);
            Assert.Equal(1920 - 480, tickOffset);
            // Tones 60 to 64 have their middle at 62, row MaxTone - 63 from the top, centred in 20 rows.
            Assert.Equal(maxTone - 62 - 0.5 - 10, trackOffset);
        }

        [Fact]
        public void PlacementPutsWideNotesNearTheLeftAndStaysInRange() {
            var wide = new[] { Note(1920, 1500, 60, "a") };
            Assert.Equal(1920 - 96, EmbeddedNotes.Placement(wide, 1920, 20, 100000, 1000).tickOffset);

            var early = new[] { Note(100, 240, 127, "a") };
            var (tickOffset, trackOffset) = EmbeddedNotes.Placement(early, 1920, 20, 100000, 1000);
            Assert.Equal(0, tickOffset);
            Assert.Equal(0, trackOffset);

            var late = new[] { Note(9000, 240, 0, "a") };
            (tickOffset, trackOffset) = EmbeddedNotes.Placement(late, 1920, 20, 5000, 100);
            Assert.Equal(5000, tickOffset);
            Assert.Equal(100, trackOffset);
        }

        [Fact]
        public void PhonemesGoToTheNoteOfTheWordTheyAreSungIn() {
            var project = Project("Lead");
            var sing = Note(0, 480, 60, "sing");
            var plus = Note(480, 480, 62, "+");
            var a = Note(960, 480, 64, "a");
            var removed = Note(1440, 480, 64, "gone");
            plus.Extends = sing;
            var part = Part(project, 0, 0, 1920, sing, plus, a);
            // OpenUtau gives a word's phonemes to its first note.
            part.phonemes.Add(Phoneme(sing, -60, "s"));
            part.phonemes.Add(Phoneme(sing, 0, "ih"));
            part.phonemes.Add(Phoneme(sing, 600, "ng"));
            part.phonemes.Add(Phoneme(a, 960, "a"));
            // Left from before the note was removed, while phonemes are pending.
            part.phonemes.Add(Phoneme(removed, 1440, "g"));

            var byNote = EmbeddedNotes.PhonemesByNote(part);
            Assert.Equal(new[] { "s", "ih" }, byNote[sing].Select(p => p.phoneme));
            Assert.Equal(new[] { "ng" }, byNote[plus].Select(p => p.phoneme));
            Assert.Equal(new[] { "a" }, byNote[a].Select(p => p.phoneme));
            Assert.Equal(3, byNote.Count);
        }

        [Fact]
        public void JsonListsEachTracksNotesWithTheirPhonemes() {
            var project = Project("Lead", "Backing");
            var ka = Note(0, 480, 60, "か");
            var plus = Note(480, 480, 62, "+");
            plus.Extends = ka;
            var part = Part(project, 0, 1920, 1920, ka, plus);
            part.phonemes.Add(Phoneme(ka, -30, "k"));
            // The user typed "ah" over the phonemizer's "a".
            part.phonemes.Add(Phoneme(ka, 0, "a", "ah"));
            // Notes come by start across parts, even when parts are out of order in the project.
            var earlier = Part(project, 0, 0, 960, Note(0, 480, 55, "la"));
            project.parts.Remove(earlier);
            project.parts.Add(earlier);

            var bytes = EmbeddedNotes.PhonemesJson(project);
            // Lyrics stay readable in the UTF-8 rather than escaped.
            Assert.Contains("\"lyric\":\"か\"", Encoding.UTF8.GetString(bytes));
            using var json = JsonDocument.Parse(bytes);
            var rootElement = json.RootElement;
            Assert.False(rootElement.GetProperty("pending").GetBoolean());
            var tracks = rootElement.GetProperty("tracks");
            Assert.Equal(2, tracks.GetArrayLength());
            var lead = tracks[0];
            Assert.Equal(0, lead.GetProperty("index").GetInt32());
            Assert.Equal("Lead", lead.GetProperty("name").GetString());
            Assert.False(lead.GetProperty("pending").GetBoolean());
            var notes = lead.GetProperty("notes");
            Assert.Equal(new[] { 0, 1920, 2400 }, notes.EnumerateArray().Select(n => n.GetProperty("tick").GetInt32()));
            var first = notes[1];
            Assert.Equal(480, first.GetProperty("duration").GetInt32());
            Assert.Equal(60, first.GetProperty("tone").GetInt32());
            Assert.Equal("か", first.GetProperty("lyric").GetString());
            var phonemes = first.GetProperty("phonemes");
            Assert.Equal(2, phonemes.GetArrayLength());
            Assert.Equal("k", phonemes[0].GetProperty("symbol").GetString());
            Assert.Equal(1890, phonemes[0].GetProperty("tick").GetInt32());
            Assert.Equal("ah", phonemes[1].GetProperty("symbol").GetString());
            Assert.Equal("a", phonemes[1].GetProperty("raw").GetString());
            Assert.Equal(0, notes[2].GetProperty("phonemes").GetArrayLength());
            Assert.Equal("Backing", tracks[1].GetProperty("name").GetString());
            Assert.Equal(0, tracks[1].GetProperty("notes").GetArrayLength());
        }

        [Fact]
        public void JsonIsPendingWithoutAProject() {
            Assert.Equal("{\"pending\":true,\"tracks\":[]}", Encoding.UTF8.GetString(EmbeddedNotes.PhonemesJson(null)));
        }
    }
}
