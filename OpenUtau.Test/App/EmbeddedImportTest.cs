using System;
using System.IO;
using System.Linq;
using OpenUtau.App.Embedding;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Test.App {
    public class EmbeddedImportTest : IDisposable {
        // A SynthV 2 project, as SynthV writes it on one line, with two tracks: a group reference moved by a bar and
        // transposed up a tone, and a second tempo.
        const string Svp = "{\"version\":197,\"time\":{\"meter\":[{\"index\":0,\"numerator\":3,\"denominator\":4}]," +
            "\"tempo\":[{\"position\":0,\"bpm\":90.0},{\"position\":2822400000,\"bpm\":120.0}]}," +
            "\"library\":[{\"uuid\":\"verse\",\"name\":\"Verse\",\"notes\":[" +
            "{\"onset\":0,\"duration\":705600000,\"lyrics\":\"la\",\"pitch\":60}]}]," +
            "\"tracks\":[{\"name\":\"Lead\",\"mainGroup\":{\"uuid\":\"lead\",\"name\":\"main\",\"notes\":[" +
            "{\"onset\":0,\"duration\":705600000,\"lyrics\":\"hel\",\"pitch\":64}," +
            "{\"onset\":705600000,\"duration\":705600000,\"lyrics\":\"AP\",\"pitch\":64}]}," +
            "\"mainRef\":{\"groupID\":\"lead\",\"blickOffset\":0,\"pitchOffset\":0,\"database\":{\"name\":\"Kevin\"}," +
            "\"voice\":{\"mouthOpening\":0}}}," +
            "{\"name\":\"Harmony\",\"mainGroup\":{\"uuid\":\"harmony\",\"name\":\"main\",\"notes\":[]}," +
            "\"mainRef\":{\"groupID\":\"harmony\",\"blickOffset\":0,\"database\":{\"name\":\"Mai\"}}," +
            "\"groups\":[{\"groupID\":\"verse\",\"blickOffset\":2116800000,\"pitchOffset\":2,\"database\":{\"name\":\"Mai\"}}]}]}";

        readonly string folder = Path.Combine(Path.GetTempPath(), "EmbeddedImportTest-" + Guid.NewGuid());

        public EmbeddedImportTest() => Directory.CreateDirectory(folder);

        public void Dispose() => Directory.Delete(folder, true);

        string Write(string name, string text) {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, text);
            return path;
        }

        [Fact]
        public void ImportSavesTheSvpAsAUstxWithItsTracksNotesAndTempo() {
            var source = Write("song.svp", Svp + "\0");
            var destination = Path.Combine(folder, "song.ustx");

            EmbeddedHost.Import(source, destination);

            var project = Ustx.Load(destination);
            Assert.Equal(new[] { "Lead", "Harmony" }, project.tracks.Select(track => track.TrackName));
            Assert.Equal(new[] { (0, 90.0), (1920, 120.0) }, project.tempos.Select(tempo => (tempo.position, tempo.bpm)));
            Assert.Equal((3, 4), (project.timeSignatures[0].beatPerBar, project.timeSignatures[0].beatUnit));
            var notes = project.parts.OfType<UVoicePart>()
                .SelectMany(part => part.notes.Select(note => (part.trackNo, part.position + note.position, note.tone, note.lyric)));
            Assert.Equal(new[] { (0, 0, 64, "hel"), (0, 480, 64, "AP"), (1, 1440, 62, "la") }, notes);
            Assert.False(File.Exists(destination + ".importing"));
        }

        [Fact]
        public void ImportLeavesADestinationThatExistsAsItWas() {
            var source = Write("song.svp", Svp);
            var destination = Write("song.ustx", "mine");

            Assert.Throws<IOException>(() => EmbeddedHost.Import(source, destination));

            Assert.Equal("mine", File.ReadAllText(destination));
        }

        [Fact]
        public void ImportWritesNothingForAFileItCantRead() {
            var source = Write("song.svp", "{\"version\":197,\"tracks\":");
            var destination = Path.Combine(folder, "song.ustx");

            Assert.ThrowsAny<Exception>(() => EmbeddedHost.Import(source, destination));

            Assert.Empty(Directory.GetFiles(folder, "song.ustx*"));
        }
    }
}
