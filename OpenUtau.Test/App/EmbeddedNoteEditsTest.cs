using System.Text.Json;
using OpenUtau.App.Embedding;
using OpenUtau.Core.Ustx;
using Xunit;
namespace OpenUtau.Test.App {
    public class EmbeddedNoteEditsTest {
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
