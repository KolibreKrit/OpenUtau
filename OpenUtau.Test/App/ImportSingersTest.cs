using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using OpenUtau.Api;
using OpenUtau.App.Embedding;
using OpenUtau.Classic;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Plugin.Builtin;
using Xunit;

namespace OpenUtau.Test.App {
    [Collection(RenderSingletonCollection.Name)]
    public class ImportSingersTest {
        class Singer : USinger {
            public Singer() { found = true; loaded = true; }
            public override string Id => "import-choice-test";
            public override string Name => "Chosen singer";
            public override USingerType SingerType => USingerType.Classic;
            public override IList<USubbank> Subbanks { get; } = new List<USubbank>();
            public override string DefaultPhonemizer => typeof(ArpasingPhonemizer).FullName;
        }

        sealed class SingerWithoutPhonemizer : Singer {
            public override string DefaultPhonemizer => "";
        }

        static ClassicSinger Bank(string name, Type? phonemizer = null) {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            VoicebankLoader.IsTest = true;
            var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var voicebank = new Voicebank { File = Path.Join(folder, "Files", name, "character.txt"), BasePath = folder };
            VoicebankLoader.LoadVoicebank(voicebank);
            voicebank.DefaultPhonemizer = phonemizer == null ? "" : PhonemizerFactory.Get(phonemizer).Id;
            return new ClassicSinger(voicebank);
        }

        static UProject Song(params string[] lyrics) {
            var project = new UProject();
            var part = new UVoicePart { trackNo = 0 };
            var position = 0;
            foreach (var lyric in lyrics) {
                var note = project.CreateNote(64, position, 480);
                note.lyric = lyric;
                part.notes.Add(note);
                position += 480;
            }
            project.parts.Add(part);
            return project;
        }

        // The phonemizer a track starts on when a singer is chosen for it in a language, and what the import reports
        // that singer can't sing.
        static (Phonemizer phonemizer, string[] unsung) Choose(ClassicSinger singer, UProject project, string language, string phonemizer = "") {
            SingerManager.Inst.Singers.Add(singer.Id, singer);
            try {
                ImportSingers.Select(project, JsonSerializer.Serialize(new {
                    tracks = new[] { new { svcoverSinger = new { id = singer.Id, phonemizer, language } } }
                }));
                using var report = JsonDocument.Parse(ImportSingers.ReportOf(project));
                var track = report.RootElement.GetProperty("tracks")[0];
                Assert.Equal(project.tracks[0].Phonemizer.Id, track.GetProperty("phonemizer").GetString());
                Assert.Equal(project.tracks[0].Phonemizer.Tag, track.GetProperty("phonemizerName").GetString());
                return (project.tracks[0].Phonemizer, track.GetProperty("unsung").EnumerateArray().Select(word => word.GetString()!).ToArray());
            } finally {
                SingerManager.Inst.Singers.Remove(singer.Id);
            }
        }

        [Fact]
        public void AJapaneseSingerChosenForAnEnglishTrackSingsItThroughEnToJa() {
            var (phonemizer, unsung) = Choose(Bank("ja_cv"), Song("hello", "world", "think", "love [l ah v]"), "english");
            Assert.IsType<ENtoJAPhonemizer>(phonemizer);
            Assert.Empty(unsung);
        }

        // A bank of the aliases given and no others.
        static ClassicSinger BankOf(params string[] aliases) {
            var folder = Path.Combine(Path.GetTempPath(), "ImportSingers-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "character.txt"), "name=Few aliases\n");
            File.WriteAllText(Path.Combine(folder, "character.yaml"), "text_file_encoding: utf-8\n");
            File.WriteAllLines(Path.Combine(folder, "oto.ini"), aliases.Select(alias => $"a.wav={alias},0,0,0,0,0"));
            try {
                var voicebank = new Voicebank { File = Path.Combine(folder, "character.txt"), BasePath = Path.GetTempPath() };
                VoicebankLoader.LoadVoicebank(voicebank);
                var singer = new ClassicSinger(voicebank);
                singer.EnsureLoaded();
                return singer;
            } finally {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void TheReportChecksAWordAfterTheNoteBeforeIt() {
            var enToJa = PhonemizerFactory.Get(typeof(ENtoJAPhonemizer)).Id;
            var aliases = new[] { "- あ", "a あ", "a と", "o う", "u R" };
            Assert.Empty(Choose(BankOf(aliases), Song("ah", "to"), "english", enToJa).unsung);
            Assert.Equal(new[] { "to" }, Choose(BankOf(aliases), Song("to"), "english", enToJa).unsung);
        }

        [Fact]
        public void TheReportSaysWhenATrackSingsItsClosestSoundsAndLeavesOutWhatIsNoWord() {
            var project = Song("hello", "-", "R", "br", "world");
            var (_, unsung) = Choose(Bank("ja_cv"), project, "english");
            Assert.Empty(unsung);
            using var report = JsonDocument.Parse(ImportSingers.ReportOf(project));
            Assert.True(report.RootElement.GetProperty("tracks")[0].GetProperty("closestSounds").GetBoolean());

            var (_, own) = Choose(Bank("ja_cv", typeof(JapanesePresampPhonemizer)), Song("は", "-", "R", "br", "zxqv"), "japanese");
            Assert.Equal(new[] { "zxqv" }, own);
        }

        [Fact]
        public void AnEnglishBankWithoutADefaultSingsAnEnglishTrackThroughItsOwnSymbols() {
            var (phonemizer, unsung) = Choose(Bank("en_delta7"), Song("love"), "english");
            Assert.IsType<EnXSampaPhonemizer>(phonemizer);
            Assert.Empty(unsung);
        }

        [Fact]
        public void ASingerOfTheTracksLanguageKeepsItsPhonemizer() {
            var (phonemizer, _) = Choose(Bank("ja_cv", typeof(ArpasingPhonemizer)), Song("hello"), "english");
            Assert.IsType<ArpasingPhonemizer>(phonemizer);
        }

        [Fact]
        public void APhonemizerChosenByHandStaysAndTheReportNamesWhatItCantSing() {
            var factory = PhonemizerFactory.Get(typeof(ArpasingPhonemizer));
            var (phonemizer, unsung) = Choose(Bank("ja_cv"), Song("hello", "world", "hello"), "english", factory.Id);
            Assert.IsType<ArpasingPhonemizer>(phonemizer);
            Assert.Equal(new[] { "hello", "world" }, unsung);
        }

        [Fact]
        public void ASingerNoPhonemizerFitsKeepsItsOwnAndTheReportNamesItsWords() {
            var (phonemizer, unsung) = Choose(Bank("zh_cvvc", typeof(ChineseCVVCPhonemizer)), Song("hello", "world"), "english");
            Assert.IsType<ChineseCVVCPhonemizer>(phonemizer);
            Assert.Equal(new[] { "hello", "world" }, unsung);
        }

        [Fact]
        public void AJapaneseTrackOnAJapaneseBankWithoutADefaultKeepsWhatSingsIt() {
            var (phonemizer, unsung) = Choose(Bank("ja_cv"), Song("は", "か"), "japanese");
            Assert.IsType<DefaultPhonemizer>(phonemizer);
            Assert.Empty(unsung);
        }

        [Fact]
        public void AChosenSingerWithoutAPhonemizerDoesNotInheritAnotherSingers() {
            var singer = new SingerWithoutPhonemizer();
            SingerManager.Inst.Singers.Add(singer.Id, singer);
            try {
                var project = new UProject();
                project.tracks[0].Phonemizer = PhonemizerFactory.Get(typeof(ArpasingPhonemizer)).Create();
                ImportSingers.Select(project, JsonSerializer.Serialize(new {
                    tracks = new[] { new { svcoverSinger = new { id = singer.Id, phonemizer = "" } } }
                }));
                Assert.IsType<DefaultPhonemizer>(project.tracks[0].Phonemizer);
            } finally {
                SingerManager.Inst.Singers.Remove(singer.Id);
            }
        }

        [Fact]
        public void AChoiceSetsOnlyItsTrackAndKeepsTheDefaultOnOtherTracks() {
            var singer = new Singer();
            var factory = PhonemizerFactory.Get(typeof(JapaneseVCVPhonemizer));
            SingerManager.Inst.Singers.Add(singer.Id, singer);
            try {
                var project = new UProject();
                project.tracks.Add(new UTrack(project));
                using var catalog = JsonDocument.Parse(ImportSingers.Catalog());
                Assert.Equal("svcover.openutau-import-singers/1", catalog.RootElement.GetProperty("schema").GetString());
                Assert.Contains(catalog.RootElement.GetProperty("singers").EnumerateArray(),
                    entry => entry.GetProperty("id").GetString() == singer.Id
                        && entry.GetProperty("name").GetString() == singer.Name);
                var original = project.tracks[0].Phonemizer;
                ImportSingers.Select(project, JsonSerializer.Serialize(new {
                    tracks = new object[] { new { }, new {
                        svcoverSinger = new { id = singer.Id, phonemizer = factory.Id }
                    } }
                }));
                Assert.Same(original, project.tracks[0].Phonemizer);
                Assert.Same(singer, project.tracks[1].Singer);
                Assert.Equal(factory.Id, project.tracks[1].Phonemizer.Id);
                var folder = Path.Combine(Path.GetTempPath(), "ImportSinger-" + Guid.NewGuid());
                Directory.CreateDirectory(folder);
                try {
                    var svp = JsonNode.Parse(EmbeddedImportTest.Svp)!;
                    svp["tracks"]![1]!["svcoverSinger"] = JsonSerializer.SerializeToNode(new { id = singer.Id, phonemizer = factory.Id });
                    var source = Path.Combine(folder, "song.svp");
                    File.WriteAllText(source, svp.ToJsonString());
                    var destination = Path.Combine(folder, "song.ustx");
                    var importedNames = EmbeddedHost.Import(source, destination);
                    using var report = JsonDocument.Parse(importedNames);
                    Assert.Equal("svcover.openutau-imported-singers/1", report.RootElement.GetProperty("schema").GetString());
                    Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("tracks")[0].GetProperty("singer").ValueKind);
                    Assert.Equal("Chosen singer", report.RootElement.GetProperty("tracks")[1].GetProperty("singer").GetString());
                    var imported = global::OpenUtau.Core.Format.Ustx.Load(destination);
                    Assert.Equal(singer.Id, imported.tracks[1].Singer.Id);
                    Assert.Equal(factory.Id, imported.tracks[1].Phonemizer.Id);
                    svp["tracks"]![1]!["svcoverSinger"]!["id"] = "not-installed";
                    File.WriteAllText(source, svp.ToJsonString());
                    var rejected = Path.Combine(folder, "rejected.ustx");
                    Assert.Throws<InvalidOperationException>(() => EmbeddedHost.Import(source, rejected));
                    Assert.False(File.Exists(rejected));
                } finally { Directory.Delete(folder, true); }
            } finally {
                SingerManager.Inst.Singers.Remove(singer.Id);
            }
        }
    }
}
