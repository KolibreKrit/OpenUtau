using System;
using System.Linq;
using System.Collections.Generic;
using OpenUtau.Core;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using YamlDotNet.Core;
namespace OpenUtau.App.Embedding {
    public static class EmbeddedNoteEdits {
        sealed class Identity { public readonly string Id = Guid.NewGuid().ToString(); }
        static readonly ConditionalWeakTable<UVoicePart, Identity> identities = new();
        public static string Id(UVoicePart part) => identities.GetOrCreateValue(part).Id;
        static readonly JsonSerializerOptions skipNull = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        // With states, each note also gives its whole state as text an edit can bring it back from. It's kept apart as a
        // snapshot can be read often and most readers want only timing and words.
        public static string Read(UProject project, int track, int from, int to, Func<UNote, bool>? selected = null, bool states = false) =>
            JsonSerializer.Serialize(EmbeddedNotes.VoiceParts(project, track).SelectMany((part, reference) => {
                var phonemes = EmbeddedNotes.PhonemesByNote(part);
                return part.notes.Select((note, index) => new {
                    track, reference, group = Id(part), offset = part.position, index,
                    position = note.position, duration = note.duration, tone = note.tone, lyric = note.lyric,
                    selected = selected?.Invoke(note) ?? false,
                    sung = string.Join(" ", phonemes[note].Select(p => p.phoneme)),
                    state = states ? Yaml.DefaultSerializer.Serialize(note) : null
                });
            }).Where(n => n.offset + n.position >= from && n.offset + n.position < to), skipNull);
        public static string Snapshot(UProject project, Func<UNote, bool>? selected = null, int? statesOf = null) => JsonSerializer.Serialize(new {
            tracks = project.tracks.Select(t => t.TrackName),
            references = Enumerable.Range(0, project.tracks.Count).SelectMany(track =>
                EmbeddedNotes.VoiceParts(project, track).Select((part, reference) => new {
                    track, reference, group = Id(part), offset = part.position, muted = project.tracks[track].Mute
                })),
            notes = Enumerable.Range(0, project.tracks.Count).SelectMany(track =>
                JsonSerializer.Deserialize<JsonElement[]>(Read(project, track, 0, int.MaxValue, selected, track == statesOf))!)
        });
        public sealed record PhonemeOffset(int note, int index, int offset);
        public sealed record PhonemeOffsets(int track, PhonemeOffset[] phonemes);
        /// <summary>The commands that give phonemes of a track these offsets from where the phonemizer puts them. A
        /// request reads <c>{"track":0,"phonemes":[{"note":1920,"index":0,"offset":-30}]}</c>, with each phoneme's word's
        /// first note by its start, and its place there, as <see cref="EmbeddedNotes.PhonemesJson"/> gives them. Nothing
        /// when the request isn't that, or a note isn't the track's.</summary>
        public static List<PhonemeOffsetCommand>? OffsetCommands(UProject project, string request) {
            PhonemeOffsets? offsets;
            try { offsets = JsonSerializer.Deserialize<PhonemeOffsets>(request); }
            catch (JsonException) { return null; }
            if (offsets?.phonemes == null) return null;
            var words = new Dictionary<int, (UVoicePart part, UNote note)>();
            foreach (var part in EmbeddedNotes.VoiceParts(project, offsets.track)) {
                foreach (var note in part.notes) {
                    if (note.Extends == null) words.TryAdd(part.position + note.position, (part, note));
                }
            }
            var commands = new List<PhonemeOffsetCommand>();
            foreach (var phoneme in offsets.phonemes) {
                if (phoneme.index < 0 || !words.TryGetValue(phoneme.note, out var word)) return null;
                commands.Add(new PhonemeOffsetCommand(word.part, word.note, phoneme.index, phoneme.offset));
            }
            return commands;
        }
        public sealed record Value(int position, int duration, int tone, string lyric, string phonemes = "", string group = "", int offset = 0, int from = 0, string? state = null);
        public sealed record Edit(int track, string group, int offset, Value[] before, Value[] after, Value[]? source);
        // A note added to its part can't be taken back, and validating it there throws on any of these.
        static bool Whole(UNote? note) => note?.pitch?.data != null && note.pitch.data.Count > 0 && !note.pitch.data.Contains(null!)
            && note.vibrato != null && note.phonemeOverrides != null && !note.phonemeOverrides.Contains(null!)
            && note.phonemeExpressions != null && note.phonemeExpressions.All(e => e?.abbr != null);
        public static string Apply(UProject project, string edits) {
            Edit[]? batch;
            try { batch = JsonSerializer.Deserialize<Edit[]>(edits); }
            catch (JsonException) { return "bad-request"; }
            if (batch == null || batch.Length == 0 || batch.Length > 256) return "bad-request";
            var plans = new List<(UVoicePart part, List<UNote> before, List<UNote> after)>();
            var used = new HashSet<UNote>();
            bool same(UNote note, Value value) => note.position == value.position && note.duration == value.duration
                && note.tone == value.tone && note.lyric == value.lyric && string.IsNullOrEmpty(value.phonemes);
            foreach (var edit in batch) {
                var part = project.parts.OfType<UVoicePart>().FirstOrDefault(p => Id(p) == edit.group && p.trackNo == edit.track);
                if (part == null || part.position != edit.offset) return "changed";
                if (edit.before == null || edit.after == null || edit.before.Length > 4096 || edit.after.Length > 4096) return "bad-request";
                var before = new List<UNote>();
                foreach (var value in edit.before) {
                    var found = part.notes.Where(n => same(n, value)).ToList();
                    if (found.Count != 1 || !used.Add(found[0])) return "changed";
                    before.Add(found[0]);
                }
                foreach (var value in edit.source ?? Array.Empty<Value>()) {
                    var sourcePart = project.parts.OfType<UVoicePart>().FirstOrDefault(p => Id(p) == value.group);
                    if (sourcePart == null || sourcePart.position != value.offset || sourcePart.notes.Count(n => same(n, value)) != 1) return "changed";
                }
                var after = new List<UNote>();
                for (var i = 0; i < edit.after.Length; ++i) {
                    var value = edit.after[i];
                    if (value.position < 0 || value.duration <= 0 || value.tone < 0 || value.tone > 127
                        || string.IsNullOrWhiteSpace(value.lyric) || value.lyric.Length > 1024
                        || !string.IsNullOrEmpty(value.phonemes) || (long)value.position + value.duration > int.MaxValue) return "bad-request";
                    // state brings back a note the app kept, and from names one of before to copy. With neither it's a new note.
                    UNote? note;
                    if (value.state != null) {
                        try { note = Yaml.DefaultDeserializer.Deserialize<UNote>(value.state); }
                        catch (YamlException) { return "bad-request"; }
                        if (!Whole(note)) return "bad-request";
                        note.AfterLoad(project, project.tracks[part.trackNo], part);
                    } else {
                        note = value.from >= 0 && value.from < before.Count ? before[value.from].Clone() : project.CreateNote();
                    }
                    note.position = value.position; note.duration = value.duration; note.tone = value.tone; note.lyric = value.lyric;
                    if (part.notes.Any(n => !before.Contains(n) && n.position < note.End && note.position < n.End)
                        || after.Any(n => n.position < note.End && note.position < n.End)) return "occupied";
                    after.Add(note);
                }
                plans.Add((part, before, after));
            }
            foreach (var together in plans.GroupBy(p => p.part)) {
                var notes = together.SelectMany(p => p.after).OrderBy(n => n.position).ToArray();
                for (var i = 1; i < notes.Length; ++i) {
                    if (notes[i].position < notes[i - 1].End) return "occupied";
                }
            }
            DocManager.Inst.StartUndoGroup("command.note.lyric");
            try {
                foreach (var (part, before, after) in plans) {
                    if (before.Count > 0) DocManager.Inst.ExecuteCmd(new RemoveNoteCommand(part, before));
                    if (after.Count > 0) DocManager.Inst.ExecuteCmd(new AddNoteCommand(part, after.OrderBy(n => n.position).ToList()));
                }
            } finally { DocManager.Inst.EndUndoGroup(); }
            return "ok";
        }
    }
}
