using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenUtau.Api;
using OpenUtau.Classic;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Plugin.Builtin;

namespace OpenUtau.App.Embedding {
    public static class ImportSingers {
        // The phonemizers that may read a track's language for a singer without it, from the bank's own recordings of it.
        static readonly Dictionary<string, Type[]> Readers = new() {
            ["EN"] = new[] { typeof(EnXSampaPhonemizer), typeof(ArpasingPlusPhonemizer), typeof(ArpasingPhonemizer), typeof(EnglishVCCVPhonemizer) },
            ["JA"] = new[] { typeof(JapanesePresampPhonemizer) },
        };

        // EN to JA leaves out what a bank lacks, so finding its aliases says nothing about a bank without kana: on a
        // Chinese bank, "hello" comes out as a vowel or two.
        static bool SingsKana(USinger singer) => new[] { "あ", "- あ", "a あ" }.Any(alias => singer.TryGetMappedOto(alias, 60, "", out _));

        public static string ReportOf(UProject project) => JsonSerializer.Serialize(new {
            schema = "svcover.openutau-imported-singers/1",
            tracks = project.tracks.Select((track, number) => new { track = track.TrackName,
                singer = track.Singer?.Found == true ? track.Singer.LocalizedName : null,
                phonemizer = track.Phonemizer?.Id, phonemizerName = track.Phonemizer?.Tag,
                closestSounds = track.Phonemizer is ENtoJAPhonemizer,
                unsung = track.Singer?.Found == true && track.Phonemizer != null
                    ? Unsung(project, number, track.Singer, track.Phonemizer).Distinct() : Enumerable.Empty<string>() })
        });

        static readonly HashSet<string> Silent = new() { "", "-", "R", "r", "br" };

        // Each word of a track's notes with the + notes it sings on, as OpenUtau gives them to a phonemizer.
        static List<Phonemizer.Note[]> Words(UProject project, int number) {
            var words = new List<List<Phonemizer.Note>>();
            foreach (var part in project.parts.OfType<UVoicePart>().Where(part => part.trackNo == number)) {
                foreach (var note in part.notes) {
                    var lyric = note.lyric ?? "";
                    string? hint = null;
                    lyric = Regex.Replace(lyric, @"\[(.*)\]", match => { hint = match.Groups[1].Value.Trim(); return ""; }).Trim();
                    var next = new Phonemizer.Note {
                        lyric = lyric, phoneticHint = hint!, tone = note.tone, position = part.position + note.position, duration = note.duration,
                        phonemeAttributes = new[] { new Phonemizer.PhonemeAttributes { index = 0, consonantStretchRatio = 1, voiceColor = "" } } };
                    if (lyric.StartsWith("+") && words.Count > 0) words.Last().Add(next);
                    else if (!Silent.Contains(lyric)) words.Add(new List<Phonemizer.Note> { next });
                }
            }
            return words.Select(word => word.ToArray()).ToList();
        }

        // The words of a track a voicebank has no samples for, by what its phonemizer asks of it. Other kinds of singer
        // sing whatever their own phonemizers give, so only a voicebank is asked.
        static List<string> Unsung(UProject project, int number, USinger singer, Phonemizer phonemizer) {
            if (singer is not ClassicSinger) return new List<string>();
            singer.EnsureLoaded();
            var unsung = new List<string>();
            var words = Words(project, number);
            var track = project.tracks[number];
            var axis = new TimeAxis();
            axis.BuildSegments(project);
            var checking = PhonemizerFactory.Get(phonemizer.GetType()).Create();
            try {
                checking.SetSinger(singer);
                checking.SetTiming(axis);
                checking.SetUp(words.ToArray(), project, track);
            } catch (Exception) {
                // What a phonemizer can't set up says nothing of the words, and the import goes on without it.
                return unsung;
            }
            for (var i = 0; i < words.Count; i++) {
                var word = words[i];
                // The notes around it as OpenUtau gives them, as a phonemizer picks a word's first sound from the note before.
                var before = i > 0 ? words[i - 1] : null;
                var after = i < words.Count - 1 ? words[i + 1] : null;
                var touchesBefore = before != null && before.Last().position + before.Last().duration >= word[0].position;
                var touchesAfter = after != null && word.Last().position + word.Last().duration >= after[0].position;
                try {
                    var phonemes = checking.Process(word, before?[0], after?[0], touchesBefore ? before![0] : null,
                        touchesAfter ? after![0] : null, touchesBefore ? before! : Array.Empty<Phonemizer.Note>()).phonemes;
                    if (phonemes.Length > 0 && phonemes.All(phoneme => singer.TryGetMappedOto(phoneme.phoneme, word[0].tone, "", out _))) continue;
                } catch (Exception) {
                    // A phonemizer for other words refuses some it can't read, which leaves them unsung.
                }
                unsung.Add(word[0].lyric);
            }
            return unsung;
        }

        // The singer's phonemizer, or what sings more of a track in a language it doesn't have: the first with the fewest
        // words the singer can't sing, or its own if none beats it.
        static PhonemizerFactory? ReaderFor(UProject project, int number, USinger singer, PhonemizerFactory? own, string? language) {
            var code = language switch { "english" => "EN", "japanese" => "JA", _ => "" };
            if (code == "" || own?.language == code || !Readers.TryGetValue(code, out var readers)) return own;
            var best = own;
            var fewest = Unsung(project, number, singer, (own ?? PhonemizerFactory.Get(typeof(DefaultPhonemizer))).Create()).Count;
            singer.EnsureLoaded();
            var kana = code == "EN" && SingsKana(singer) ? new[] { typeof(ENtoJAPhonemizer) } : Array.Empty<Type>();
            foreach (var reader in readers.Concat(kana)) {
                if (fewest == 0) break;
                var factory = PhonemizerFactory.Get(reader);
                var unsung = Unsung(project, number, singer, factory.Create()).Count;
                if (unsung < fewest) { best = factory; fewest = unsung; }
            }
            return best;
        }

        public static string Catalog() => JsonSerializer.Serialize(new {
            schema = "svcover.openutau-import-singers/1",
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
                var language = choice.TryGetProperty("language", out var given) ? given.GetString() : null;
                var factory = string.IsNullOrEmpty(phonemizerId) ? ReaderFor(project, index - 1, singer, SingerManager.PhonemizerFor(singer), language)
                    : PhonemizerFactory.Get(phonemizerId)
                        ?? throw new InvalidOperationException($"Phonemizer is no longer installed: {phonemizerId}");
                track.Singer = singer;
                track.Phonemizer = (factory ?? PhonemizerFactory.Get(typeof(DefaultPhonemizer))).Create();
                track.RendererSettings.Renderer = Core.Render.Renderers.CreateRenderer(Core.Render.Renderers.GetDefaultRenderer(singer.SingerType));
            }
        }
    }
}
