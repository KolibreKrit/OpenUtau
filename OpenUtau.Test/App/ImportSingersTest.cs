using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json;
using OpenUtau.Api;
using OpenUtau.App.Embedding;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Plugin.Builtin;
using Xunit;

namespace OpenUtau.Test.App {
    [Collection(RenderSingletonCollection.Name)]
    public class ImportSingersTest {
        sealed class Singer : USinger {
            public Singer() { found = true; loaded = true; }
            public override string Id => "import-choice-test";
            public override string Name => "Chosen singer";
            public override USingerType SingerType => USingerType.Classic;
            public override IList<USubbank> Subbanks { get; } = new List<USubbank>();
            public override string DefaultPhonemizer => typeof(ArpasingPhonemizer).FullName;
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
                    EmbeddedHost.Import(source, destination);
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
