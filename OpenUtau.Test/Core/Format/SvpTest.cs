using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;

using OpenUtau.Api;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using OpenUtau.Plugin.Builtin;

namespace OpenUtau.Core.Format {
    [Collection(RenderSingletonCollection.Name)]
    public class SvpTest : IDisposable {
        readonly string basePath;
        readonly string folder = Path.Combine(Path.GetTempPath(), "SvpTest-" + Guid.NewGuid());
        readonly List<string> installed = new List<string>();
        readonly List<string> recentSingers = Preferences.Default.RecentSingers.ToList();
        readonly Dictionary<string, string> singerPhonemizers = new Dictionary<string, string>(Preferences.Default.SingerPhonemizers);

        public SvpTest() {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            basePath = Path.Join(dir, "Files", "Svp");
            Directory.CreateDirectory(folder);
            // As OpenUtau does when it starts: voicebanks' text is in Shift JIS, and it finds these phonemizers in its
            // plugins.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            PhonemizerFactory.Get(typeof(ArpasingPhonemizer));
            PhonemizerFactory.Get(typeof(JapaneseVCVPhonemizer));
        }

        public void Dispose() {
            Directory.Delete(folder, true);
            foreach (var singer in installed) {
                Directory.Delete(singer, true);
            }
            if (installed.Count > 0) {
                SingerManager.Inst.SearchAllSingers();
            }
            Preferences.Default.RecentSingers = recentSingers;
            Preferences.Default.SingerPhonemizers = singerPhonemizers;
        }

        // Installs a voicebank whose own phonemizer is the given one, or none, and returns its id.
        string Install(string name, Type? phonemizer) {
            var singer = Path.Combine(PathManager.Inst.SingersPath, name);
            Directory.CreateDirectory(singer);
            installed.Add(singer);
            File.WriteAllText(Path.Combine(singer, "character.txt"), $"name={name}\n");
            if (phonemizer != null) {
                File.WriteAllText(Path.Combine(singer, "character.yaml"), $"default_phonemizer: {phonemizer.FullName}\n");
            }
            SingerManager.Inst.SearchAllSingers();
            return SingerManager.Inst.Singers.Values.Single(s => s.Name == name).Id;
        }

        // A SynthV project with a note on each track, whose voice sings a language, or the one chosen for it.
        string WriteSvp(params (string name, string language, string languageOverride)[] tracks) {
            var path = Path.Combine(folder, "song.svp");
            File.WriteAllText(path, JsonSerializer.Serialize(new {
                version = 153,
                time = new {
                    meter = new[] { new { index = 0, numerator = 4, denominator = 4 } },
                    tempo = new[] { new { position = 0, bpm = 120.0 } },
                },
                library = Array.Empty<object>(),
                tracks = tracks.Select(track => new {
                    track.name,
                    mainGroup = new {
                        uuid = track.name, name = "main",
                        notes = new[] { new { onset = 0, duration = 705600000, lyrics = "la", pitch = 60 } },
                    },
                    mainRef = new {
                        groupID = track.name, blickOffset = 0,
                        database = new { name = "SynthV voice", track.language, track.languageOverride },
                    },
                }),
            }));
            return path;
        }

        // The SVP's track of that name. It isn't always first, since a new project starts with an empty track.
        static UTrack Track(UProject project, string name) =>
            project.tracks.Single(track => track.TrackName == name);

        static (string?, string, string?) Setup(UTrack track) =>
            (track.Singer.Found ? track.Singer.Id : null, track.Phonemizer.Id, track.RendererSettings.renderer);

        UWavePart LoadWavePart(string file) {
            var project = SVP.Load(Path.Join(basePath, file));
            return Assert.Single(project.parts.OfType<UWavePart>());
        }

        [Fact]
        public void AudioTrackLoads() {
            // A wave part with no peaks crashes the part control that binds to them,
            // and one with no duration renders as zero length.
            var part = LoadWavePart("instrumental.svp");

            Assert.NotNull(part.Peaks);
            Assert.DoesNotContain("[Missing]", part.DisplayName);
            Assert.True(part.fileDurationMs > 0);
            Assert.True(part.Duration > 0);
        }

        [Fact]
        public void TracksAreTheSvpsOwn() {
            // No empty track from the new project comes before them.
            var project = SVP.Load(Path.Join(basePath, "instrumental.svp"));

            Assert.Equal(new[] { "Audio" }, project.tracks.Select(track => track.TrackName));
            Assert.Equal(0, Assert.Single(project.parts).trackNo);
        }

        [Fact]
        public void MissingAudioKeepsDeclaredDuration() {
            // The SVP declares the audio length, so a track whose file is absent keeps
            // its place and its length instead of being dropped on import.
            var part = LoadWavePart("missing-audio.svp");

            Assert.NotNull(part.Peaks);
            Assert.Contains("[Missing]", part.DisplayName);
            Assert.Equal(50, part.fileDurationMs, 3);
            Assert.True(part.Duration > 0);
        }

        [Fact]
        public void EachTrackStartsWithAnInstalledSingerOfItsVoicesLanguage() {
            // OpenUtau would otherwise render nothing until someone chose each track's singer.
            var english = Install("English singer", typeof(ArpasingPhonemizer));
            var japanese = Install("Japanese singer", typeof(JapaneseVCVPhonemizer));

            var project = SVP.Load(WriteSvp(("Lead", "english", ""), ("Harmony", "english", "japanese")));

            var renderer = Renderers.GetDefaultRenderer(USingerType.Classic);
            Assert.Equal((english, typeof(ArpasingPhonemizer).FullName, renderer), Setup(Track(project, "Lead")));
            Assert.Equal((japanese, typeof(JapaneseVCVPhonemizer).FullName, renderer), Setup(Track(project, "Harmony")));
        }

        [Fact]
        public void ATrackStartsWithTheSingerOfItsLanguageChosenLast() {
            var alto = Install("Alto", typeof(ArpasingPhonemizer));
            var bass = Install("Bass", typeof(ArpasingPhonemizer));
            var japanese = Install("Japanese singer", typeof(JapaneseVCVPhonemizer));
            var svp = WriteSvp(("Lead", "english", ""));
            Assert.Equal(alto, Track(SVP.Load(svp), "Lead").Singer.Id);

            Preferences.Default.RecentSingers = new List<string> { japanese, bass };

            Assert.Equal(bass, Track(SVP.Load(svp), "Lead").Singer.Id);
        }

        [Fact]
        public void ASingerSingsTheLanguageOfThePhonemizerChosenForIt() {
            // Until then it sings its own phonemizer's English, and no installed singer sings the track's Japanese.
            var singer = Install("Singer", typeof(ArpasingPhonemizer));
            var svp = WriteSvp(("Lead", "japanese", ""));
            Assert.Equal((null, typeof(DefaultPhonemizer).FullName, null), Setup(Track(SVP.Load(svp), "Lead")));

            Preferences.Default.SingerPhonemizers[singer] = typeof(JapaneseVCVPhonemizer).FullName!;

            var renderer = Renderers.GetDefaultRenderer(USingerType.Classic);
            Assert.Equal((singer, typeof(JapaneseVCVPhonemizer).FullName, renderer),
                Setup(Track(SVP.Load(svp), "Lead")));
        }
    }
}
