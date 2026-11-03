using System;
using System.Linq;
using System.Text.Json;
using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

namespace OpenUtau.App.Embedding {
    public static class ImportSingers {
        public static string Catalog() => JsonSerializer.Serialize(new {
            singers = SingerManager.Inst.Singers.Values.OrderBy(singer => singer.LocalizedName).Select(singer => {
                var phonemizer = SingerManager.PhonemizerFor(singer);
                return new { id = singer.Id, name = singer.LocalizedName, language = phonemizer?.language ?? "",
                    phonemizer = phonemizer?.Id ?? "" };
            }),
            phonemizers = PhonemizerFactory.GetAll().Select(factory => new { id = factory.Id, name = factory.ToString() }),
            defaults = new[] { "EN", "JA", "ZH", "KO", "ES" }.ToDictionary(language => language,
                language => SingerManager.Inst.SingerFor(language)?.Id ?? "")
        });

        public static void Select(UProject project, string json) {
            using var document = JsonDocument.Parse(json.TrimEnd('\0'));
            if (!document.RootElement.TryGetProperty("tracks", out var tracks)) return;
            int index = 0;
            foreach (var source in tracks.EnumerateArray()) {
                if (index >= project.tracks.Count) throw new InvalidOperationException("Import track count changed");
                var track = project.tracks[index++];
                if (!source.TryGetProperty("svcoverSinger", out var choice)) continue;
                var id = choice.GetProperty("id").GetString() ?? "";
                if (!SingerManager.Inst.Singers.TryGetValue(id, out var singer))
                    throw new InvalidOperationException($"Singer is no longer installed: {id}");
                var phonemizerId = choice.GetProperty("phonemizer").GetString();
                var factory = string.IsNullOrEmpty(phonemizerId) ? SingerManager.PhonemizerFor(singer)
                    : PhonemizerFactory.Get(phonemizerId)
                        ?? throw new InvalidOperationException($"Phonemizer is no longer installed: {phonemizerId}");
                track.Singer = singer;
                if (factory != null) track.Phonemizer = factory.Create();
                track.RendererSettings.Renderer = Core.Render.Renderers.CreateRenderer(Core.Render.Renderers.GetDefaultRenderer(singer.SingerType));
            }
        }
    }
}
