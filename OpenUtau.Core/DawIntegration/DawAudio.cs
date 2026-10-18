using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using K4os.Hash.xxHash;
using OpenUtau.Core.SignalChain;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.DawIntegration {
    /// <summary>
    /// Data-plane helpers: part audio extraction, XXH64 hashing and binary frame framing.
    /// See PROTOCOL.md §5.2 and §6.1 (<c>getAudio</c>).
    /// </summary>
    public static class DawAudio {
        /// <summary>Hard engine limit, mirrors <c>PlaybackManager.cs:35</c>. Never negotiated (PROTOCOL.md §1).</summary>
        public const int SampleRate = 44100;
        public const int Channels = 2;

        /// <summary>Data-plane frame header prefix. A received line starting with this is binary, not control.</summary>
        public const string FramePrefix = "audio ";

        /// <summary>
        /// Upper bound on a single data-plane frame payload (256 MiB = 44.1 kHz stereo float32,
        /// roughly 12.7 minutes of audio). Frames are length-prefixed by the peer, so before this
        /// bound a hostile or corrupted header could make the receiver allocate up to 2 GiB
        /// (PROTOCOL.md §6.1). Parts larger than this cannot be shipped over the wire in v1.
        /// </summary>
        public const int MaxFrameBytes = 1 << 28;

        /// <summary>
        /// Converts project time to an index into OpenUtau's interleaved stereo sample space.
        /// The signal chain is addressed in absolute project samples, two floats per frame
        /// (see <c>SampleSlot.Offset</c>).
        /// </summary>
        public static int MsToInterleavedIndex(double ms) {
            if (ms <= 0) {
                return 0;
            }
            return (int)(ms * SampleRate / 1000) * Channels;
        }

        /// <summary>
        /// Extracts the rendered audio covering a part's own tick range, from the
        /// transport's per-phrase slot registry (<see cref="PlaybackManager.MixPlanner")).
        /// </summary>
        /// <remarks>
        /// Voice parts only. <see cref="UWavePart"/> audio is out of v1 scope: it is user-supplied
        /// material the DAW can import directly, and its source is user WAV data rather
        /// than the render pipeline.
        /// </remarks>
        /// <returns>
        /// False when the part is unrendered or the render has not finished it. Callers must
        /// skip such parts rather than shipping partial audio, because the resulting hash
        /// would be wrong for the finished audio.
        /// </returns>
        public static bool TryExtractPart(UProject project, UVoicePart part, out float[] samples) {
            samples = Array.Empty<float>();
            OpenUtau.Core.Render.RenderPartRequest? request;
            try {
                // Wait for the latest phrase build so the extraction targets
                // the current phrases.
                part.WaitPhraseSource(TimeSpan.FromSeconds(10));
                request = part.GetRenderRequest();
            } catch {
                // An invalid part simply cannot be extracted.
                return false;
            }
            if (request == null) {
                return false;
            }
            var placements = new (ulong hash, double startMs, double endMs)[request.phrases.Length];
            for (int i = 0; i < request.phrases.Length; ++i) {
                (placements[i].startMs, placements[i].endMs) = request.phrases[i].Layout.Range;
                placements[i].hash = request.phrases[i].hash;
            }
            return TryExtractPart(project, part, PlaybackManager.Inst.MixPlanner, placements, out samples);
        }

        internal static bool TryExtractPart(UProject project, UVoicePart part, MixPlanner planner,
                IEnumerable<(ulong hash, double startMs, double endMs)> placements, out float[] samples) {
            samples = Array.Empty<float>();
            placements = placements.ToArray();
            // Completeness gate: a part is extractable only once every current phrase
            // has rendered pcm — the planner's IsPartReady carries that invariant.
            if (!planner.IsPartReady(part, placements.Select(p => p.hash))) {
                return false;
            }
            return TryMix(planner, part, placements, project.timeAxis.TickPosToMsPos(part.position),
                project.timeAxis.TickPosToMsPos(part.End), out samples);
        }

        /// <summary>How far a voice part's audio has got, as <see cref="ReadPart"/> finds it.</summary>
        public enum PartState {
            /// <summary>Its phonemes, phrases or render aren't finished.</summary>
            Rendering,
            /// <summary>Every one of its current phrases has rendered.</summary>
            Ready,
            /// <summary>It has nothing to render: no phrases, or a muted track that OpenUtau is set to skip.</summary>
            Silent,
        }

        /// <summary>A voice part's audio as <see cref="ReadPart"/> found it.</summary>
        public sealed class PartAudio {
            public PartState State { get; internal set; }
            /// <summary>Names the audio <see cref="TryMix"/> gives, from the part's window and the hash and place of
            /// each phrase, so the same phrases in the same window have the same key. 0 unless the part is ready.</summary>
            public ulong Key { get; internal set; }
            internal MixPlanner Planner = null!;
            internal UPart Part = null!;
            internal (ulong hash, double startMs, double endMs)[] Phrases = Array.Empty<(ulong, double, double)>();
            internal double StartMs, EndMs;

            /// <summary>The part's audio as <see cref="TryExtractPart(UProject, UVoicePart, out float[])"/> gives
            /// it, from the phrases read. Safe on any thread. False if the part wasn't ready, or the planner has since
            /// dropped a phrase's audio, as a failed render or clearing the phrase cache does.</summary>
            public bool TryMix(out float[] samples) {
                samples = Array.Empty<float>();
                return State == PartState.Ready && DawAudio.TryMix(Planner, Part, Phrases, StartMs, EndMs, out samples);
            }
        }

        /// <summary>
        /// Reads where a voice part's audio has got without waiting for the phonemizer, a phrase build or a render.
        /// Runs on the document thread once the project is validated, so the part's phonemes and phrases follow its
        /// last edit. A part waiting for any of those is <see cref="PartState.Rendering"/>, and nothing tells when its
        /// phrase build lands, so a caller waiting for it reads again.
        /// </summary>
        public static PartAudio ReadPart(UProject project, UVoicePart part, MixPlanner planner) {
            var rendering = new PartAudio { State = PartState.Rendering };
            if (Util.Preferences.Default.SkipRenderingMutedTracks
                && part.trackNo >= 0 && part.trackNo < project.tracks.Count && project.tracks[part.trackNo].Muted) {
                return new PartAudio { State = PartState.Silent };
            }
            if (!part.PhonemesUpToDate || !part.WaitPhraseSource(TimeSpan.Zero)) {
                return rendering;
            }
            var phrases = part.GetRenderRequest().phrases;
            var placements = new (ulong hash, double startMs, double endMs)[phrases.Length];
            for (int i = 0; i < phrases.Length; ++i) {
                (placements[i].startMs, placements[i].endMs) = phrases[i].Layout.Range;
                placements[i].hash = phrases[i].hash;
            }
            return ReadPart(planner, part, placements,
                project.timeAxis.TickPosToMsPos(part.position), project.timeAxis.TickPosToMsPos(part.End));
        }

        internal static PartAudio ReadPart(MixPlanner planner, UPart part,
                (ulong hash, double startMs, double endMs)[] phrases, double startMs, double endMs) {
            if (phrases.Length == 0) {
                return new PartAudio { State = PartState.Silent };
            }
            // Until a render begins after an edit, the planner's session is the last render's, with every slot ready,
            // so each phrase must also have audio of its own.
            if (!planner.IsPartReady(part, phrases.Select(p => p.hash))
                || !phrases.All(p => planner.TryGetPhrasePcm(part, p.hash, out _))) {
                return new PartAudio { State = PartState.Rendering };
            }
            var key = new byte[16 + phrases.Length * 24];
            BinaryPrimitives.WriteDoubleLittleEndian(key.AsSpan(0), startMs);
            BinaryPrimitives.WriteDoubleLittleEndian(key.AsSpan(8), endMs);
            for (int i = 0; i < phrases.Length; ++i) {
                var at = key.AsSpan(16 + i * 24);
                BinaryPrimitives.WriteUInt64LittleEndian(at, phrases[i].hash);
                BinaryPrimitives.WriteDoubleLittleEndian(at.Slice(8), phrases[i].startMs);
                BinaryPrimitives.WriteDoubleLittleEndian(at.Slice(16), phrases[i].endMs);
            }
            return new PartAudio {
                State = PartState.Ready,
                Key = XXH64.DigestOf(key),
                Planner = planner,
                Part = part,
                Phrases = phrases,
                StartMs = startMs,
                EndMs = endMs,
            };
        }

        static bool TryMix(MixPlanner planner, UPart part,
                IEnumerable<(ulong hash, double startMs, double endMs)> placements, double startMs, double endMs,
                out float[] samples) {
            samples = Array.Empty<float>();
            // A phrase whose audio the planner has dropped since, as a failed render or clearing the cache does, would
            // leave a gap.
            if (!MixPlanner.TryGetPartPlacements(planner, part, placements, out var pcmList)
                || pcmList.Count != placements.Count()) {
                return false;
            }
            int start = MsToInterleavedIndex(startMs);
            int end = MsToInterleavedIndex(endMs);
            int count = end - start;
            if (count <= 0) {
                return false;
            }
            // TryGetPartPlacements returns only rendered placements, so this transient slot set
            // mixes like the finished part.
            var slots = new SampleSlot[pcmList.Count];
            for (int i = 0; i < pcmList.Count; i++) {
                var p = pcmList[i];
                slots[i] = new SampleSlot(p.posMs, p.durMs, 0, p.channels, p.pcm, SlotState.Ready);
            }
            var source = new SlotMixSource();
            source.SetSlots(slots);
            // ISignalSource.Mix adds into the buffer, so it must start zeroed.
            var buffer = new float[count];
            source.Mix(start, buffer, 0, count);
            // §6.1 pre-fader output trim: OpenUtau pans constant-power, so a mix that
            // bypasses panning sits a systematic 3 dB above what the performance was tuned
            // against. Scale by cos(π/4) before hashing and serving; the trim is not mixer
            // state and never follows volume, pan or muted.
            float trim = MathF.Sqrt(0.5f);
            for (int i = 0; i < count; i++) {
                buffer[i] *= trim;
            }
            samples = buffer;
            return true;
        }

        /// <summary>Serializes samples as raw little-endian float32, the fixed wire payload (PROTOCOL.md §6.1).</summary>
        public static byte[] ToPcmBytes(float[] samples) {
            var bytes = new byte[samples.Length * sizeof(float)];
            if (BitConverter.IsLittleEndian) {
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            } else {
                for (int i = 0; i < samples.Length; i++) {
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), samples[i]);
                }
            }
            return bytes;
        }

        /// <summary>Reads raw little-endian float32 back into samples. Used by tests and conformance tooling.</summary>
        public static float[] FromPcmBytes(byte[] bytes) {
            if (bytes.Length % sizeof(float) != 0) {
                throw new DawProtocolException($"PCM payload of {bytes.Length} bytes is not a whole number of float32 samples.");
            }
            var samples = new float[bytes.Length / sizeof(float)];
            if (BitConverter.IsLittleEndian) {
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            } else {
                for (int i = 0; i < samples.Length; i++) {
                    samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
                }
            }
            return samples;
        }

        public static ulong Hash(byte[] pcm) => XXH64.DigestOf(pcm);

        /// <summary>
        /// Hashes are always decimal strings on the wire — a 64-bit value exceeds the 2^53
        /// safe-integer range of JSON number parsers (PROTOCOL.md §5.2).
        /// </summary>
        public static string FormatHash(ulong hash) => hash.ToString(CultureInfo.InvariantCulture);

        public static bool TryParseHash(string? text, out ulong hash) {
            return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out hash);
        }

        /// <summary>Builds the <c>audio &lt;hash&gt; &lt;length&gt;\n</c> header. The payload follows verbatim.</summary>
        public static byte[] BuildFrameHeader(string hash, int length) {
            return Encoding.UTF8.GetBytes($"{FramePrefix}{hash} {length.ToString(CultureInfo.InvariantCulture)}\n");
        }

        public static bool IsFrameHeader(string line) => line.StartsWith(FramePrefix, StringComparison.Ordinal);

        /// <summary>
        /// Parses a data-plane header line (without the trailing newline). Rejects negative,
        /// unparseable or oversized lengths so a malformed header cannot desynchronize the stream
        /// and a hostile length cannot drive the receiver into a huge allocation (§8).
        /// </summary>
        public static bool TryParseFrameHeader(string line, out string hash, out int length) {
            hash = string.Empty;
            length = 0;
            if (!IsFrameHeader(line)) {
                return false;
            }
            var parts = line.Substring(FramePrefix.Length).Split(' ');
            if (parts.Length != 2) {
                return false;
            }
            if (!TryParseHash(parts[0], out _)) {
                return false;
            }
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out length)) {
                return false;
            }
            if (length > MaxFrameBytes) {
                return false;
            }
            hash = parts[0];
            return true;
        }
    }
}
