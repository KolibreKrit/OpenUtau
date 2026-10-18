using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenUtau.Core.Ustx;

namespace OpenUtau.App.Embedding {
    /// <summary>
    /// What <see cref="EmbeddedHost"/>'s note entry points work out from a project: which part to show, where
    /// to scroll, and what OpenUtau sings. Kept apart from the host so tests can call it without a native app.
    /// Ticks here are absolute, from the project's start, unless a name says otherwise.
    /// </summary>
    public static class EmbeddedNotes {
        /// <summary>A track's voice parts, earliest first.</summary>
        public static List<UVoicePart> VoiceParts(UProject project, int trackNo) =>
            project.parts.OfType<UVoicePart>().Where(p => p.trackNo == trackNo).OrderBy(p => p.position).ToList();

        /// <summary>The track's voice part under a tick, the latest starting if parts overlap there. Otherwise the
        /// part that ends last before the tick, or the first part if they all start after it. Null if the track
        /// has no voice part.</summary>
        public static UVoicePart? PartAt(UProject project, int trackNo, int tick) {
            var parts = VoiceParts(project, trackNo);
            return parts.LastOrDefault(p => p.position <= tick && tick < p.End)
                ?? parts.Where(p => p.End <= tick).OrderBy(p => p.End).LastOrDefault()
                ?? parts.FirstOrDefault();
        }

        /// <summary>
        /// Finds a track's notes by their start ticks. The part holding the first tick that names a note is the
        /// one shown, and the notes are those of that part starting at any of the ticks, in the part's order.
        /// Notes the ticks name in other parts are left out. Null if no tick names a note.
        /// </summary>
        public static (UVoicePart part, List<UNote> notes)? FindNotes(UProject project, int trackNo, IReadOnlyList<int> ticks) {
            var parts = VoiceParts(project, trackNo);
            foreach (var tick in ticks) {
                var part = parts.FirstOrDefault(p => p.notes.Any(n => p.position + n.position == tick));
                if (part != null) {
                    var wanted = ticks.ToHashSet();
                    return (part, part.notes.Where(n => wanted.Contains(part.position + n.position)).ToList());
                }
            }
            return null;
        }

        /// <summary>
        /// Where to scroll the piano roll to show notes as the app's SynthV script does. Their start sits a
        /// quarter of the way across, or 5% from the left if they take more than 0.7 of the view's width, and
        /// their pitches are centred. Offsets are the piano roll's, in ticks from the part's start and in rows
        /// from the top, clamped to its scroll ranges.
        /// </summary>
        public static (double tickOffset, double trackOffset) Placement(IReadOnlyCollection<UNote> notes,
                double viewportTicks, double viewportTracks, double maxTickOffset, double maxTrackOffset) {
            int start = notes.Min(n => n.position);
            int end = notes.Max(n => n.End);
            double lead = end - start > 0.7 * viewportTicks ? 0.05 : 0.25;
            double tickOffset = start - lead * viewportTicks;
            // A tone's row is MaxTone - tone - 1 from the top, so the middle of the range is half a row below.
            double middleRow = ViewConstants.MaxTone - (notes.Min(n => n.tone) + notes.Max(n => n.tone)) / 2.0 - 0.5;
            double trackOffset = middleRow - viewportTracks / 2;
            return (Math.Clamp(tickOffset, 0, Math.Max(0, maxTickOffset)), Math.Clamp(trackOffset, 0, Math.Max(0, maxTrackOffset)));
        }

        /// <summary>
        /// A part's sung phonemes by note. OpenUtau gives a word's phonemes to its first note, and a word runs on
        /// into the + notes after it. Each phoneme goes to the last note of its word starting at or before the
        /// phoneme, so a phoneme sung during a + note is that note's, and one sung before the word's first note,
        /// as a consonant is, belongs to the first note. Notes OpenUtau doesn't phonemize have none.
        /// </summary>
        public static Dictionary<UNote, List<UPhoneme>> PhonemesByNote(UVoicePart part) {
            var byNote = part.notes.ToDictionary(n => n, _ => new List<UPhoneme>());
            var words = new Dictionary<UNote, List<UNote>>();
            foreach (var note in part.notes) {
                var first = note.Extends ?? note;
                if (!words.TryGetValue(first, out var word)) {
                    words[first] = word = new List<UNote>();
                }
                word.Add(note);
            }
            foreach (var phoneme in part.phonemes) {
                // While phonemes are pending, some may still belong to notes since removed.
                if (phoneme.Parent == null || !words.TryGetValue(phoneme.Parent, out var word)) {
                    continue;
                }
                var note = word[0];
                foreach (var each in word) {
                    if (each.position <= phoneme.position) {
                        note = each;
                    }
                }
                byNote[note].Add(phoneme);
            }
            return byNote;
        }

        /// <summary>
        /// What OpenUtau sings, as UTF-8 JSON:
        /// <c>{"pending":false,"tracks":[{"index":0,"name":"Lead","pending":false,"notes":[{"tick":1920,
        /// "duration":480,"tone":60,"lyric":"sing","phonemes":[{"symbol":"s","raw":"s","tick":1900}]}]}]}</c>.
        /// Tracks come in project order and notes by start. <c>symbol</c> is what OpenUtau shows and sings, with
        /// any phoneme the user typed over it; <c>raw</c> is what the phonemizer gave. A track is pending while
        /// OpenUtau phonemizes any of its parts, when its phonemes may be out of date.
        /// </summary>
        public static byte[] PhonemesJson(UProject? project) {
            using var stream = new MemoryStream();
            // Lyrics and phonemes stay readable UTF-8 rather than escaped.
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) {
                json.WriteStartObject();
                var tracks = project?.tracks ?? new List<UTrack>();
                var partsByTrack = tracks.Select((_, i) => VoiceParts(project!, i)).ToList();
                json.WriteBoolean("pending", project == null || partsByTrack.Any(parts => parts.Any(p => !p.PhonemesUpToDate)));
                json.WriteStartArray("tracks");
                for (int i = 0; i < tracks.Count; ++i) {
                    var parts = partsByTrack[i];
                    json.WriteStartObject();
                    json.WriteNumber("index", i);
                    json.WriteString("name", tracks[i].TrackName);
                    json.WriteBoolean("pending", parts.Any(p => !p.PhonemesUpToDate));
                    json.WriteStartArray("notes");
                    var notes = parts.SelectMany(part => {
                        var phonemes = PhonemesByNote(part);
                        return part.notes.Select(note => (part, note, phonemes: phonemes[note]));
                    }).OrderBy(e => e.part.position + e.note.position);
                    foreach (var (part, note, phonemes) in notes) {
                        json.WriteStartObject();
                        json.WriteNumber("tick", part.position + note.position);
                        json.WriteNumber("duration", note.duration);
                        json.WriteNumber("tone", note.tone);
                        json.WriteString("lyric", note.lyric);
                        json.WriteStartArray("phonemes");
                        foreach (var phoneme in phonemes) {
                            json.WriteStartObject();
                            json.WriteString("symbol", phoneme.phoneme);
                            json.WriteString("raw", phoneme.rawPhoneme);
                            json.WriteNumber("tick", part.position + phoneme.position);
                            json.WriteEndObject();
                        }
                        json.WriteEndArray();
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            return stream.ToArray();
        }
    }
}
