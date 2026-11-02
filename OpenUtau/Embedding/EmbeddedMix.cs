using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia.Threading;
using OpenUtau.Core;
using OpenUtau.Core.DawIntegration;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.App.Embedding {
    /// <summary>
    /// Hands the application the project, its tracks and each voice part's rendered audio as they change, in place of
    /// the DAW Integration's socket and its debounce. After each change the application gets a snapshot, and it reads
    /// the audio of each part it doesn't have yet by the hash the snapshot names. A part renders again after each edit
    /// to it, and while it does the snapshot names its last audio and marks it outdated, and the application keeps
    /// playing that.
    /// </summary>
    public static class EmbeddedMix {
        /// <summary>Edits come in bursts, such as a note dragged, so a snapshot with a new song waits this long after
        /// the last one.</summary>
        static readonly TimeSpan songInterval = TimeSpan.FromMilliseconds(100);
        /// <summary>Nothing tells when a phrase build lands, so while a part waits for its render the parts are read
        /// again this often. A part a failed pass left counts too, as a build can still land and make it current or
        /// leave it nothing to sing.</summary>
        static readonly TimeSpan renderingInterval = TimeSpan.FromMilliseconds(250);

        static Action<string>? receiver;
        static Subscriber? subscriber;
        static bool scheduled, polling, documentChanged = true;
        // OpenUtau's last render pass failed, so the parts it left unrendered wait for another pass, which an edit,
        // Undo or Redo starts. The application's playback doesn't, as OpenUtau doesn't play.
        static bool renderFailed;
        static DateTime songSentAt = DateTime.MinValue;
        static string? lastSent;
        // The last audio each part rendered, which the snapshot names, marked outdated, while it renders again.
        static readonly Dictionary<UVoicePart, DawAudio.PartAudio> rendered = new();
        // The audio the last snapshot named, by hash, which ReadAudio may be asked for from any thread.
        static readonly object namedLock = new();
        static Dictionary<string, DawAudio.PartAudio> named = new();

        sealed class Subscriber : ICmdSubscriber {
            public void OnNext(UCommand cmd, bool isUndo) {
                switch (cmd) {
                    case LoadProjectNotification:
                        rendered.Clear();
                        documentChanged = true;
                        renderFailed = false;
                        break;
                    case ToastNotification toast when toast.translationKey == "errors.failed.prerender":
                        renderFailed = true;
                        break;
                    case PreRenderNotification:
                        renderFailed = false;
                        break;
                    case VolumeChangeNotification:
                    case PanChangeNotification:
                    case SoloTrackNotification:
                        documentChanged = true;
                        break;
                    case PartRenderedNotification:
                    case PhonemizedNotification:
                    case WaveformReadyNotification:
                    case SaveProjectNotification:
                        break;
                    case UNotification:
                        // Play position, selection and progress change nothing the snapshot holds.
                        return;
                    default:
                        documentChanged = true;
                        renderFailed = false;
                        break;
                }
                Schedule();
            }
        }

        /// <summary>Takes the function snapshots go to, or null to stop. See <see cref="EmbeddedHost.SetMix"/>.</summary>
        public static void Listen(Action<string>? function) {
            receiver = function;
            lastSent = null;
            documentChanged = true;
            if (subscriber == null) {
                subscriber = new Subscriber();
                DocManager.Inst.AddSubscriber(subscriber);
            }
            Schedule();
        }

        /// <summary>Sends a snapshot soon, once OpenUtau is ready and the current command has finished.</summary>
        public static void Schedule() {
            if (scheduled || receiver == null || !EmbeddedHost.Usable) {
                return;
            }
            scheduled = true;
            var wait = documentChanged ? songSentAt + songInterval - DateTime.UtcNow : TimeSpan.Zero;
            if (wait > TimeSpan.Zero) {
                DispatcherTimer.RunOnce(Send, wait, DispatcherPriority.Background);
            } else {
                Dispatcher.UIThread.Post(Send, DispatcherPriority.Background);
            }
        }

        static void Send() {
            scheduled = false;
            // The project being opened sends its first snapshot once it has, so none tells of the last one meanwhile.
            if (receiver == null || !EmbeddedHost.Usable) {
                return;
            }
            string json;
            int waiting;
            try {
                // An edit OpenUtau validates later would leave its part looking up to date until then.
                DocManager.Inst.FlushValidation();
                (json, waiting) = Snapshot(DocManager.Inst.Project, documentChanged);
            } catch (Exception e) {
                Log.Error(e, "Embedded OpenUtau couldn't read its parts");
                return;
            }
            if (documentChanged) {
                documentChanged = false;
                songSentAt = DateTime.UtcNow;
            }
            if (waiting > 0 && !polling) {
                polling = true;
                DispatcherTimer.RunOnce(() => {
                    polling = false;
                    Schedule();
                }, renderingInterval, DispatcherPriority.Background);
            }
            if (json == lastSent) {
                return;
            }
            lastSent = json;
            receiver(json);
        }

        /// <summary>The snapshot <see cref="EmbeddedHost.SetMix"/> describes, and how many parts wait for a render:
        /// those it counts as rendering, or as failed if a failed render pass left them.</summary>
        public static (string json, int waiting) Snapshot(UProject project, bool withSong) {
            var planner = PlaybackManager.Inst.MixPlanner;
            var now = new Dictionary<string, DawAudio.PartAudio>();
            var parts = project.parts.OfType<UVoicePart>().OrderBy(p => p.trackNo).ThenBy(p => p.position).ToList();
            foreach (var gone in rendered.Keys.Except(parts).ToList()) {
                rendered.Remove(gone);
            }
            int rendering = 0;
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) {
                json.WriteStartObject();
                bool saved = !string.IsNullOrEmpty(project.FilePath);
                json.WriteString("name", saved ? Path.GetFileNameWithoutExtension(project.FilePath) : string.Empty);
                json.WriteBoolean("saved", saved);
                if (EmbeddedHost.Unopened is string unopened) {
                    json.WriteString("unopened", unopened);
                }
                if (withSong) {
                    WriteSong(json, project);
                }
                json.WriteStartArray("tracks");
                foreach (var track in project.tracks) {
                    json.WriteStartObject();
                    json.WriteString("name", track.TrackName);
                    json.WriteString("singer", track.Singer?.Name ?? string.Empty);
                    json.WriteString("engine", track.RendererSettings.renderer ?? string.Empty);
                    json.WriteNumber("volume", track.Volume);
                    json.WriteNumber("pan", track.Pan);
                    json.WriteBoolean("muted", track.Muted);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartArray("parts");
                foreach (var part in parts) {
                    var audio = DawAudio.ReadPart(project, part, planner);
                    bool outdated = false;
                    if (audio.State == DawAudio.PartState.Ready) {
                        rendered[part] = audio;
                    } else if (audio.State == DawAudio.PartState.Silent) {
                        rendered.Remove(part);
                    } else {
                        ++rendering;
                        outdated = true;
                    }
                    if (!rendered.TryGetValue(part, out var heard)) {
                        continue;
                    }
                    var hash = DawAudio.FormatHash(heard.Key);
                    now[hash] = heard;
                    json.WriteStartObject();
                    json.WriteNumber("trackNo", part.trackNo);
                    json.WriteNumber("startMs", project.timeAxis.TickPosToMsPos(part.position));
                    json.WriteNumber("endMs", project.timeAxis.TickPosToMsPos(part.End));
                    json.WriteString("audioHash", hash);
                    // Once it renders again it loses this, even with the same audio, so the snapshot changes.
                    if (outdated) {
                        json.WriteBoolean("outdated", true);
                    }
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteNumber("rendering", renderFailed ? 0 : rendering);
                json.WriteNumber("failed", renderFailed ? rendering : 0);
                json.WriteEndObject();
            }
            lock (namedLock) {
                named = now;
            }
            return (System.Text.Encoding.UTF8.GetString(stream.ToArray()), rendering);
        }

        /// <summary>The <c>song</c> <see cref="EmbeddedHost.SetMix"/> describes. It holds only what the application
        /// reads, as writing the whole USTX here takes over a second for a long project and would hold up the window
        /// after each edit.</summary>
        static void WriteSong(Utf8JsonWriter json, UProject project) {
            json.WriteStartObject("song");
            json.WriteStartArray("tempos");
            foreach (var tempo in project.tempos) {
                json.WriteStartObject();
                json.WriteNumber("position", tempo.position);
                json.WriteNumber("bpm", tempo.bpm);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("timeSignatures");
            foreach (var signature in project.timeSignatures) {
                json.WriteStartObject();
                json.WriteNumber("barPosition", signature.barPosition);
                json.WriteNumber("beatPerBar", signature.beatPerBar);
                json.WriteNumber("beatUnit", signature.beatUnit);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("tracks");
            foreach (var track in project.tracks) {
                json.WriteStartObject();
                json.WriteString("name", track.TrackName);
                json.WriteString("singer", track.Singer?.Id ?? string.Empty);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("parts");
            foreach (var part in project.parts.OfType<UVoicePart>().OrderBy(p => p.trackNo).ThenBy(p => p.position)) {
                json.WriteStartObject();
                json.WriteString("name", part.name);
                json.WriteNumber("trackNo", part.trackNo);
                json.WriteNumber("position", part.position);
                json.WriteNumber("duration", part.Duration);
                json.WriteStartArray("notes");
                foreach (var note in part.notes) {
                    json.WriteStartObject();
                    json.WriteNumber("position", note.position);
                    json.WriteNumber("duration", note.duration);
                    json.WriteNumber("tone", note.tone);
                    json.WriteString("lyric", note.lyric);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }

        /// <summary>The audio of the part the last snapshot names by the hash: 0 with its samples, or 2 if no part in
        /// the last snapshot has that hash or OpenUtau has since dropped some of its audio. Any thread.</summary>
        public static int ReadAudio(string hash, out float[] samples) {
            samples = Array.Empty<float>();
            DawAudio.PartAudio? audio;
            lock (namedLock) {
                named.TryGetValue(hash, out audio);
            }
            return audio != null && audio.TryMix(out samples) ? 0 : 2;
        }
    }
}
