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
