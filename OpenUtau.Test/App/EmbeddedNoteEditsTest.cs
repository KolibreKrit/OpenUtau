using System.Linq;
using System.Text.Json;
using OpenUtau.App.Embedding;
using OpenUtau.Core.Ustx;
using Xunit;
namespace OpenUtau.Test.App {
    public class EmbeddedNoteEditsTest {
        [Fact]
        public void OffsetCommandsFindEachPhonemesFirstNoteByItsStart() {
            var project = OpenUtau.Core.Format.Ustx.Create();
            var part = new UVoicePart { trackNo = 0, position = 960 };
            var note = project.CreateNote(60, 480, 480);
            part.notes.Add(note);
            project.parts.Add(part);
            var commands = EmbeddedNoteEdits.OffsetCommands(project, "{\"track\":0,\"phonemes\":[{\"note\":1440,\"index\":1,\"offset\":-30}]}");
            Assert.NotNull(commands);
            commands!.ForEach(command => command.Execute());
            Assert.Equal(-30, note.GetPhonemeOverride(1).offset);
            commands.ForEach(command => command.Unexecute());
            Assert.Null(note.GetPhonemeOverride(1).offset);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("{\"track\":0}")]
        [InlineData("{\"track\":0,\"phonemes\":[{\"note\":1441,\"index\":0,\"offset\":5}]}")]
        [InlineData("{\"track\":1,\"phonemes\":[{\"note\":1440,\"index\":0,\"offset\":5}]}")]
        [InlineData("{\"track\":0,\"phonemes\":[{\"note\":1440,\"index\":-1,\"offset\":5}]}")]
        public void OffsetCommandsRefuseARequestThatIsntForTheProjectsNotes(string request) {
            var project = OpenUtau.Core.Format.Ustx.Create();
            var part = new UVoicePart { trackNo = 0, position = 960 };
            part.notes.Add(project.CreateNote(60, 480, 480));
            project.parts.Add(part);
            Assert.Null(EmbeddedNoteEdits.OffsetCommands(project, request));
        }

        [Fact]
        public void SnapshotGivesStatesOnlyWhenAskedAndOnlyForThatTrack() {
            var project = OpenUtau.Core.Format.Ustx.Create();
            project.tracks.Add(new UTrack(project) { TrackNo = 1 });
            foreach (var track in new[] { 0, 1 }) {
                var part = new UVoicePart { trackNo = track, position = 0 };
                part.notes.Add(project.CreateNote(60, 0, 480));
                project.parts.Add(part);
            }
            string?[] states(string snapshot) {
                using var read = JsonDocument.Parse(snapshot);
                return read.RootElement.GetProperty("notes").EnumerateArray()
                    .Select(n => n.TryGetProperty("state", out var state) ? state.GetString() : null).ToArray();
            }
            Assert.Equal(new string?[] { null, null }, states(EmbeddedNoteEdits.Snapshot(project)));
            var asked = states(EmbeddedNoteEdits.Snapshot(project, statesOf: 1));
            Assert.Null(asked[0]);
            Assert.Contains("tone: 60", asked[1]);
        }

        [Fact]
        public void SnapshotWithStatesGivesThatTracksPitchCurveByPart() {
            var project = OpenUtau.Core.Format.Ustx.Create();
            project.tracks.Add(new UTrack(project) { TrackNo = 1 });
            foreach (var track in new[] { 0, 1 }) {
                var part = new UVoicePart { trackNo = track, position = 960 };
                part.notes.Add(project.CreateNote(60, 0, 480));
                var curve = new UCurve(project.expressions[OpenUtau.Core.Format.Ustx.PITD]);
                curve.xs.AddRange(new[] { 0, 5 });
                curve.ys.AddRange(new[] { 40 + track, -3 });
                part.curves.Add(curve);
                project.parts.Add(part);
            }
            project.parts.Add(new UVoicePart { trackNo = 1, position = 4800 });
            JsonElement[] references(string snapshot) {
                using var read = JsonDocument.Parse(snapshot);
                return read.RootElement.GetProperty("references").EnumerateArray().Select(r => r.Clone()).ToArray();
            }
            Assert.All(references(EmbeddedNoteEdits.Snapshot(project)), r => Assert.False(r.TryGetProperty("pitch", out _)));
            var asked = references(EmbeddedNoteEdits.Snapshot(project, statesOf: 1));
            Assert.False(asked[0].TryGetProperty("pitch", out _));
            int[] numbers(JsonElement r, string name) => r.GetProperty("pitch").GetProperty(name).EnumerateArray().Select(n => n.GetInt32()).ToArray();
            Assert.Equal(new[] { 0, 5 }, numbers(asked[1], "xs"));
            Assert.Equal(new[] { 41, -3 }, numbers(asked[1], "ys"));
            Assert.Empty(numbers(asked[2], "xs"));
            Assert.Empty(numbers(asked[2], "ys"));
        }

        internal static bool[] Drawn(UProject project, int track) {
            using var read = JsonDocument.Parse(EmbeddedNoteEdits.Snapshot(project, statesOf: track));
            return read.RootElement.GetProperty("references").EnumerateArray().Where(r => r.GetProperty("track").GetInt32() == track)
                .Select(r => r.GetProperty("pitch").GetProperty("drawn").GetBoolean()).ToArray();
        }

        internal static bool[] Tuned(UProject project, int track) {
            using var read = JsonDocument.Parse(EmbeddedNoteEdits.Snapshot(project, statesOf: track));
            return read.RootElement.GetProperty("notes").EnumerateArray().Where(n => n.GetProperty("track").GetInt32() == track)
                .Select(n => n.GetProperty("tuned").GetBoolean()).ToArray();
        }

        [Fact]
        public void SnapshotWithStatesSaysWhetherACurveChangedSinceItWasMarked() {
            var project = OpenUtau.Core.Format.Ustx.Create();
            var part = new UVoicePart { trackNo = 0, position = 0 };
            var curve = new UCurve(project.expressions[OpenUtau.Core.Format.Ustx.PITD]);
            curve.xs.AddRange(new[] { 0, 5 });
            curve.ys.AddRange(new[] { 40, -3 });
            part.curves.Add(curve);
            project.parts.Add(part);
            Assert.Equal(new[] { true }, Drawn(project, 0));
            EmbeddedNoteEdits.MarkPitch(project);
            Assert.Equal(new[] { false }, Drawn(project, 0));
            curve.ys[1] = -4;
            Assert.Equal(new[] { true }, Drawn(project, 0));
            curve.ys[1] = -3;
            Assert.Equal(new[] { false }, Drawn(project, 0));
            curve.xs.Add(10);
            curve.ys.Add(0);
            Assert.Equal(new[] { true }, Drawn(project, 0));
        }

        [Fact]
        public void SnapshotWithStatesSaysACurveWithoutPointsWasNotDrawnEvenUnmarked() {
            var project = OpenUtau.Core.Format.Ustx.Create();
            project.parts.Add(new UVoicePart { trackNo = 0, position = 0 });
            var part = new UVoicePart { trackNo = 0, position = 960 };
            part.curves.Add(new UCurve(project.expressions[OpenUtau.Core.Format.Ustx.PITD]));
            project.parts.Add(part);
            Assert.Equal(new[] { false, false }, Drawn(project, 0));
        }

        [Fact]
        public void SnapshotWithStatesSaysWhichNotesHaveTheirOwnTransitionsOrVibrato() {
            var project = OpenUtau.Core.Format.Ustx.Create();
            var part = new UVoicePart { trackNo = 0, position = 0 };
            var notes = Enumerable.Range(0, 6).Select(i => project.CreateNote(60, i * 480, 480)).ToArray();
            notes[1].vibrato.length = 50;
            notes[2].pitch.data[1].X = 60;
            notes[3].pitch.data[1].Y = 5;
            notes[4].pitch.snapFirst = false;
            // OpenUtau starts a snapped note's transition at the previous note's tone.
            notes[5].pitch.data[0].Y = -20;
            foreach (var note in notes) part.notes.Add(note);
            project.parts.Add(part);
            Assert.Equal(new[] { false, true, true, true, true, false }, Tuned(project, 0));
        }

        [Fact]
        public void RangeReadsLyricsAtAbsoluteTicksAcrossParts() {
            var project = OpenUtau.Core.Format.Ustx.Create();
            var part = new UVoicePart { trackNo = 0, position = 1920 };
            part.notes.Add(new UNote { position = 480, duration = 240, tone = 64, lyric = "hold" });
            part.notes.Add(new UNote { position = 960, duration = 480, tone = 67, lyric = "on" });
            project.parts.Add(part);
            using var read = JsonDocument.Parse(EmbeddedNoteEdits.Read(project, 0, 2400, 2880));
            var notes = read.RootElement;
            Assert.Equal(1, notes.GetArrayLength());
            Assert.Equal("hold", notes[0].GetProperty("lyric").GetString());
            Assert.Equal(480, notes[0].GetProperty("position").GetInt32());
            Assert.Equal(1920, notes[0].GetProperty("offset").GetInt32());
            Assert.Equal(240, notes[0].GetProperty("duration").GetInt32());
            Assert.Equal(64, notes[0].GetProperty("tone").GetInt32());
        }
    }
}
