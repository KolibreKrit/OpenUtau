using System;
using System.Linq;
using System.Collections.Generic;
using OpenUtau.Core;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenUtau.Core.Ustx;
namespace OpenUtau.App.Embedding {
    public static class EmbeddedNoteEdits {
        sealed class Identity { public readonly string Id = Guid.NewGuid().ToString(); }
        static readonly ConditionalWeakTable<UVoicePart, Identity> identities = new();
        public static string Id(UVoicePart part) => identities.GetOrCreateValue(part).Id;
        public static string Read(UProject project, int track, int from, int to, Func<UNote, bool>? selected = null) =>
            JsonSerializer.Serialize(EmbeddedNotes.VoiceParts(project, track).SelectMany((part, reference) => {
                var phonemes = EmbeddedNotes.PhonemesByNote(part);
                return part.notes.Select((note, index) => new {
                    track, reference, group = Id(part), offset = part.position, index,
                    position = note.position, duration = note.duration, tone = note.tone, lyric = note.lyric,
                    selected = selected?.Invoke(note) ?? false,
                    sung = string.Join(" ", phonemes[note].Select(p => p.phoneme))
                });
            }).Where(n => n.offset + n.position >= from && n.offset + n.position < to));
        public static string Snapshot(UProject project, Func<UNote, bool>? selected = null) => JsonSerializer.Serialize(new {
            tracks = project.tracks.Select(t => t.TrackName),
            references = Enumerable.Range(0, project.tracks.Count).SelectMany(track =>
                EmbeddedNotes.VoiceParts(project, track).Select((part, reference) => new {
                    track, reference, group = Id(part), offset = part.position, muted = project.tracks[track].Mute
                })),
            notes = Enumerable.Range(0, project.tracks.Count).SelectMany(track =>
                JsonSerializer.Deserialize<JsonElement[]>(Read(project, track, 0, int.MaxValue, selected))!)
        });
        public sealed record Value(int position, int duration, int tone, string lyric, string phonemes = "", string group = "", int offset = 0, int from = 0);
        public sealed record Edit(int track, string group, int offset, Value[] before, Value[] after, Value[]? source);
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
                    var note = value.from >= 0 && value.from < before.Count ? before[value.from].Clone() : new UNote();
                    note.position = value.position; note.duration = value.duration; note.tone = value.tone; note.lyric = value.lyric;
                    if (part.notes.Any(n => !before.Contains(n) && n.position < note.End && note.position < n.End)
                        || after.Any(n => n.position < note.End && note.position < n.End)) return "occupied";
                    after.Add(note);
                }
                plans.Add((part, before, after));
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
