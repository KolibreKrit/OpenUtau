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
                    state = states ? Yaml.DefaultSerializer.Serialize(note) : null,
                    tuned = states ? Tuned(note) : (bool?)null
                });
            }).Where(n => n.offset + n.position >= from && n.offset + n.position < to), skipNull);
        // With states, each part of that track also gives its pitch deviation curve, which a pitch span can bring back, and
        // whether someone changed the curve since the host's import or edits.
        public static string Snapshot(UProject project, Func<UNote, bool>? selected = null, int? statesOf = null) => JsonSerializer.Serialize(new {
            tracks = project.tracks.Select(t => t.TrackName),
            references = Enumerable.Range(0, project.tracks.Count).SelectMany(track =>
                EmbeddedNotes.VoiceParts(project, track).Select((part, reference) => new {
                    track, reference, group = Id(part), offset = part.position, muted = project.tracks[track].Mute,
                    pitch = track == statesOf ? PitchOf(part) : null
                })),
            notes = Enumerable.Range(0, project.tracks.Count).SelectMany(track =>
                JsonSerializer.Deserialize<JsonElement[]>(Read(project, track, 0, int.MaxValue, selected, track == statesOf))!)
        }, skipNull);
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
        static object PitchOf(UVoicePart part) {
            var curve = part.curves.FirstOrDefault(c => c.abbr == Core.Format.Ustx.PITD);
            var xs = curve?.xs ?? new List<int>();
            var ys = curve?.ys ?? new List<int>();
            return new { xs, ys, drawn = part.hostPitch != PitchMark(xs, ys) };
        }
        // A part's pitch deviation curve as UVoicePart.hostPitch marks it.
        static string PitchMark(IReadOnlyList<int> xs, IReadOnlyList<int> ys) {
            var mark = 14695981039346656037UL;
            foreach (var value in xs.Prepend(xs.Count).Concat(ys)) {
                foreach (var b in BitConverter.GetBytes(value)) mark = (mark ^ b) * 1099511628211UL;
            }
            return mark.ToString("x16");
        }
        // The SVP importer gives the host's notes transitions of 40 ms either side of their start, as a new note has by
        // default, and no vibrato. A note with other pitch points or with vibrato was tuned since.
        static bool Tuned(UNote note) => note.vibrato.length > 0 || !note.pitch.snapFirst || note.pitch.data.Count != 2
            || note.pitch.data.Zip(new[] { -40f, 40f }).Any(p => p.First.X != p.Second || p.First.Y != 0 || p.First.shape != PitchPointShape.io);
        /// <summary>Gives each voice part of a project imported for the host the mark of its pitch curve.</summary>
        public static void MarkPitch(UProject project) {
            foreach (var part in project.parts.OfType<UVoicePart>()) {
                var curve = part.curves.FirstOrDefault(c => c.abbr == Core.Format.Ustx.PITD);
                part.hostPitch = PitchMark(curve?.xs ?? new List<int>(), curve?.ys ?? new List<int>());
            }
        }
        // The host's change to a part's curve also moves the part's mark of it, if the part still had the host's curve.
        sealed class HostPitchCommand : MergedSetCurveCommand {
            readonly string? oldMark, newMark;
            public HostPitchCommand(UProject project, UVoicePart part, int[] oldXs, int[] oldYs, int[] newXs, int[] newYs)
                : base(project, part, Core.Format.Ustx.PITD, oldXs, oldYs, newXs, newYs) {
                oldMark = part.hostPitch;
                newMark = oldMark == PitchMark(oldXs, oldYs) ? PitchMark(newXs, newYs) : oldMark;
            }
            public override void Execute() { base.Execute(); Part.hostPitch = newMark; }
            public override void Unexecute() { base.Unexecute(); Part.hostPitch = oldMark; }
        }
        public sealed record Value(int position, int duration, int tone, string lyric, string phonemes = "", string group = "", int offset = 0, int from = 0, string? state = null);
        // The part's pitch deviation from one of its ticks to another, both included, becomes these points, in ticks from the
        // part's start and cents.
        public sealed record PitchSpan(int from, int to, int[] xs, int[] ys);
        public sealed record Edit(int track, string group, int offset, Value[] before, Value[] after, Value[]? source, PitchSpan[]? pitch = null);
        static bool Whole(PitchSpan? span) => span?.xs != null && span.ys != null && span.from <= span.to && span.xs.Length == span.ys.Length
            && span.xs.All(x => x >= span.from && x <= span.to) && span.xs.Zip(span.xs.Skip(1)).All(p => p.First < p.Second);
        static HostPitchCommand? PitchCommand(UProject project, UVoicePart part, PitchSpan[] spans) {
            if (spans.Length == 0 || !project.expressions.TryGetValue(Core.Format.Ustx.PITD, out var limits)) return null;
            var curve = part.curves.FirstOrDefault(c => c.abbr == Core.Format.Ustx.PITD);
            var points = (curve?.xs ?? new List<int>()).Zip(curve?.ys ?? new List<int>(), (x, y) => (x, y)).ToList();
            foreach (var span in spans) points = Replaced(points, span, limits);
            return new HostPitchCommand(project, part, curve?.xs.ToArray() ?? Array.Empty<int>(), curve?.ys.ToArray() ?? Array.Empty<int>(),
                points.Select(p => p.x).ToArray(), points.Select(p => p.y).ToArray());
        }
        // The curve interpolates between its points, and holds its default value before the first and after the last. Where
        // the span's points would change that outside the span, points just outside it keep the old curve's values, in whole
        // cents, so a slope across the span's edge can round a cent the other way there. Only those points are added, so a
        // span that puts back the old points between the nearest ones outside it adds none.
        static List<(int x, int y)> Replaced(List<(int x, int y)> points, PitchSpan span, UExpressionDescriptor limits) {
            var old = new UCurve(limits) { xs = points.Select(p => p.x).ToList(), ys = points.Select(p => p.y).ToList() };
            var next = points.Where(p => p.x < span.from || p.x > span.to)
                .Concat(span.xs.Zip(span.ys, (x, y) => (x, Math.Clamp(y, (int)limits.min, (int)limits.max)))).OrderBy(p => p.x).ToList();
            bool same(int a, int b) => a < 0 ? b < 0 : b >= 0 && points[a] == next[b];
            var anchors = new List<int>();
            var left = points.FindLastIndex(p => p.x < span.from);
            var after = points.FindIndex(p => p.x >= span.from);
            if (left >= 0 && points[left].x < span.from - 1 && !same(after, next.FindIndex(p => p.x >= span.from))) {
                if (after < 0) anchors.Add(points[left].x + 1);
                anchors.Add(span.from - 1);
            }
            var right = points.FindIndex(p => p.x > span.to);
            var before = points.FindLastIndex(p => p.x <= span.to);
            if (right >= 0 && points[right].x > span.to + 1 && !same(before, next.FindLastIndex(p => p.x <= span.to))) {
                anchors.Add(span.to + 1);
                if (before < 0) anchors.Add(points[right].x - 1);
            }
            return next.Concat(anchors.Distinct().Select(x => (x, old.Sample(x)))).OrderBy(p => p.x).ToList();
        }
        // A note added to its part can't be taken back, and validating it there throws on any of these.
        static bool Whole(UNote? note) => note?.pitch?.data != null && note.pitch.data.Count > 0 && !note.pitch.data.Contains(null!)
            && note.vibrato != null && note.phonemeOverrides != null && !note.phonemeOverrides.Contains(null!)
            && note.phonemeExpressions != null && note.phonemeExpressions.All(e => e?.abbr != null);
        public static string Apply(UProject project, string edits) {
            Edit[]? batch;
            try { batch = JsonSerializer.Deserialize<Edit[]>(edits); }
            catch (JsonException) { return "bad-request"; }
            if (batch == null || batch.Length == 0 || batch.Length > 256) return "bad-request";
            var plans = new List<(UVoicePart part, List<UNote> before, List<UNote> after, PitchSpan[] pitch)>();
            var used = new HashSet<UNote>();
            bool same(UNote note, Value value) => note.position == value.position && note.duration == value.duration
                && note.tone == value.tone && note.lyric == value.lyric && string.IsNullOrEmpty(value.phonemes);
            foreach (var edit in batch) {
                var part = project.parts.OfType<UVoicePart>().FirstOrDefault(p => Id(p) == edit.group && p.trackNo == edit.track);
                if (part == null || part.position != edit.offset) return "changed";
                if (edit.before == null || edit.after == null || edit.before.Length > 4096 || edit.after.Length > 4096
                    || edit.pitch?.All(Whole) == false) return "bad-request";
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
                plans.Add((part, before, after, edit.pitch ?? Array.Empty<PitchSpan>()));
            }
            foreach (var together in plans.GroupBy(p => p.part)) {
                var notes = together.SelectMany(p => p.after).OrderBy(n => n.position).ToArray();
                for (var i = 1; i < notes.Length; ++i) {
                    if (notes[i].position < notes[i - 1].End) return "occupied";
                }
            }
            DocManager.Inst.StartUndoGroup("command.note.lyric");
            try {
                foreach (var (part, before, after, pitch) in plans) {
                    if (before.Count > 0) DocManager.Inst.ExecuteCmd(new RemoveNoteCommand(part, before));
                    if (after.Count > 0) DocManager.Inst.ExecuteCmd(new AddNoteCommand(part, after.OrderBy(n => n.position).ToList()));
                    if (PitchCommand(project, part, pitch) is { } curve) DocManager.Inst.ExecuteCmd(curve);
                }
            } finally { DocManager.Inst.EndUndoGroup(); }
            return "ok";
        }
    }
}
