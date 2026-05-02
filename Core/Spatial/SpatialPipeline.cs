using System;
using System.Collections.Generic;
using System.Linq;

namespace KhurramAudioRoute.Core.Spatial
{
    /// <summary>
    /// Phase 3 spatial-audio pipeline. Stages run in order on a single
    /// interleaved buffer; each stage may change the channel count by
    /// returning a new <see cref="SpatialBuffer"/> with a different layout.
    ///
    /// Pipeline shape:
    ///   loopback -> Upmix -> Scene -> Room -> Renderer -> EQ/limiter (existing)
    ///
    /// Stages are pluggable so we can swap Cavern vs Steam Audio vs convolution
    /// without touching call sites. The runtime configuration is a <see cref="SpatialPreset"/>;
    /// the <see cref="SpatialPipelineFactory"/> turns a preset into a stage list.
    /// </summary>
    public sealed class SpatialPipeline
    {
        private readonly IReadOnlyList<ISpatialStage> _stages;

        public SpatialPipeline(IEnumerable<ISpatialStage> stages)
        {
            _stages = stages.ToArray();
        }

        public SpatialBuffer Process(SpatialBuffer input)
        {
            var current = input;
            foreach (var stage in _stages)
            {
                if (!stage.Enabled) continue;
                current = stage.Process(current);
            }
            return current;
        }

        public IReadOnlyList<ISpatialStage> Stages => _stages;
    }

    /// <summary>
    /// One stage of the spatial pipeline. Implementations must be deterministic
    /// for a given input + state and must not allocate per-sample on the hot path.
    /// </summary>
    public interface ISpatialStage
    {
        string Name { get; }
        bool Enabled { get; set; }
        SpatialBuffer Process(SpatialBuffer input);
    }

    /// <summary>
    /// Interleaved float audio buffer with channel layout metadata. The DSP path
    /// uses 32-bit float at the engine's working sample rate (default 48 kHz).
    /// </summary>
    public readonly struct SpatialBuffer
    {
        public float[] Samples { get; }
        public int ChannelCount { get; }
        public int SampleRate { get; }
        public ChannelLayout Layout { get; }

        public SpatialBuffer(float[] samples, int channelCount, int sampleRate, ChannelLayout layout)
        {
            Samples = samples;
            ChannelCount = channelCount;
            SampleRate = sampleRate;
            Layout = layout;
        }

        public int FrameCount => ChannelCount == 0 ? 0 : Samples.Length / ChannelCount;
    }

    public enum ChannelLayout
    {
        Mono,
        Stereo,
        Quad,
        Surround_5_1,
        Surround_7_1,
        Binaural   // stereo, but rendered with HRTF for headphones
    }

    /// <summary>Top-level user-facing spatial mode.</summary>
    public enum SpatialPreset
    {
        /// <summary>Pass-through. No spatial processing.</summary>
        Off,
        /// <summary>Cheap stereo crossfeed for headphones — no HRTF, no upmix.</summary>
        HeadphoneStereoPlus,
        /// <summary>Stereo -> 7.1 upmix, cinema layout, theater room IR, HRTF binaural.</summary>
        HeadphoneCinema,
        /// <summary>No upmix, dry, HRTF binaural with light studio room.</summary>
        HeadphoneStudio,
        /// <summary>Stereo -> 5.1 upmix, large hall convolution, HRTF binaural.</summary>
        HeadphoneConcertHall,
        /// <summary>Stereo -> 5.1 upmix, no HRTF — for actual 5.1 speaker setups.</summary>
        Speakers_5_1,
        /// <summary>Minimal spatial, lowest latency. For games.</summary>
        GameMode
    }

    public static class SpatialPipelineFactory
    {
        public static SpatialPipeline Create(SpatialPreset preset)
        {
            return preset switch
            {
                SpatialPreset.Off                  => new SpatialPipeline(new[] { (ISpatialStage)new PassthroughStage() }),
                SpatialPreset.HeadphoneStereoPlus  => new SpatialPipeline(new ISpatialStage[] { new CrossfeedStage() }),
                SpatialPreset.HeadphoneCinema      => new SpatialPipeline(new ISpatialStage[]
                {
                    new MatrixUpmixStage(targetLayout: ChannelLayout.Surround_7_1),
                    new ConvolutionRoomStage(RoomImpulseResponse.SmallTheater),
                    new CavernBinauralStage()
                }),
                SpatialPreset.HeadphoneStudio      => new SpatialPipeline(new ISpatialStage[]
                {
                    new ConvolutionRoomStage(RoomImpulseResponse.SmallStudio) { Enabled = true },
                    new CavernBinauralStage()
                }),
                SpatialPreset.HeadphoneConcertHall => new SpatialPipeline(new ISpatialStage[]
                {
                    new MatrixUpmixStage(targetLayout: ChannelLayout.Surround_5_1),
                    new ConvolutionRoomStage(RoomImpulseResponse.ConcertHall),
                    new CavernBinauralStage()
                }),
                SpatialPreset.Speakers_5_1         => new SpatialPipeline(new ISpatialStage[]
                {
                    new MatrixUpmixStage(targetLayout: ChannelLayout.Surround_5_1)
                }),
                SpatialPreset.GameMode             => new SpatialPipeline(new ISpatialStage[]
                {
                    new CavernBinauralStage { Quality = HrtfQuality.LowLatency }
                }),
                _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
            };
        }
    }

    // -----------------------------------------------------------------------
    // Stage stubs. Each one is a single-responsibility unit; fill them in as
    // you bring stages online. Keep the contract simple: float[] in, float[] out.
    // -----------------------------------------------------------------------

    public sealed class PassthroughStage : ISpatialStage
    {
        public string Name => "Passthrough";
        public bool Enabled { get; set; } = true;
        public SpatialBuffer Process(SpatialBuffer input) => input;
    }

    /// <summary>Bauer-style stereo crossfeed for headphone listening.</summary>
    public sealed class CrossfeedStage : ISpatialStage
    {
        public string Name => "Crossfeed";
        public bool Enabled { get; set; } = true;

        public SpatialBuffer Process(SpatialBuffer input)
        {
            // TODO: implement BS2B-style filter (low-shelf attenuated copy from
            // L into R and vice versa). Around 100 LOC. Reference: bs2b.sf.net.
            return input;
        }
    }

    /// <summary>
    /// Stereo -> 5.1/7.1 matrix upmix. Hafler-style derivation:
    ///   FC  = (L + R) * sqrt(0.5)   (mid; reinforces dialog)
    ///   BL  = (L - R) * sqrt(0.5)   (side; rear-left placement)
    ///   BR  = (R - L) * sqrt(0.5)   (side; rear-right placement)
    ///   LFE = 0                     (no low-pass cross-feed for v1)
    ///   For 7.1: SL/SR mirror BL/BR at -3dB.
    ///
    /// Already-multichannel input (>= 3 ch) is passed through. Mono input is
    /// duplicated to L/R first. Channel order matches Cavern's
    /// <see cref="CavernBinauralStage"/> expectations:
    ///   5.1: FL, FR, FC, LFE, BL, BR
    ///   7.1: FL, FR, FC, LFE, SL, SR, BL, BR  (sides before backs — Cavern's
    ///        convention, NOT Microsoft WaveFormatExtensible ordering).
    /// </summary>
    public sealed class MatrixUpmixStage : ISpatialStage
    {
        public string Name => "Matrix Upmix";
        public bool Enabled { get; set; } = true;
        public ChannelLayout TargetLayout { get; }

        private static readonly float Mid = (float)System.Math.Sqrt(0.5);

        public MatrixUpmixStage(ChannelLayout targetLayout)
        {
            TargetLayout = targetLayout;
        }

        public SpatialBuffer Process(SpatialBuffer input)
        {
            int outChannels = TargetLayout switch
            {
                ChannelLayout.Surround_5_1 => 6,
                ChannelLayout.Surround_7_1 => 8,
                _ => input.ChannelCount
            };

            // Bypass: already at or beyond the target layout, or no-op target.
            if (outChannels == input.ChannelCount || input.ChannelCount >= 3)
                return input;

            int frames = input.FrameCount;
            var output = new float[frames * outChannels];
            var src    = input.Samples;
            int inCh   = input.ChannelCount;

            for (int f = 0; f < frames; f++)
            {
                float L, R;
                if (inCh == 1)
                {
                    L = R = src[f];
                }
                else
                {
                    L = src[f * inCh];
                    R = src[f * inCh + 1];
                }

                float fc = (L + R) * Mid;
                float bl = (L - R) * Mid;
                float br = (R - L) * Mid;
                int o = f * outChannels;

                if (outChannels == 6)
                {
                    output[o + 0] = L;     // FL
                    output[o + 1] = R;     // FR
                    output[o + 2] = fc;    // FC
                    output[o + 3] = 0f;    // LFE
                    output[o + 4] = bl;    // BL
                    output[o + 5] = br;    // BR
                }
                else // 8 channels (7.1)
                {
                    output[o + 0] = L;            // FL
                    output[o + 1] = R;            // FR
                    output[o + 2] = fc;           // FC
                    output[o + 3] = 0f;           // LFE
                    output[o + 4] = bl * Mid;     // SL  (sides at -3dB vs backs)
                    output[o + 5] = br * Mid;     // SR
                    output[o + 6] = bl;           // BL
                    output[o + 7] = br;           // BR
                }
            }

            return new SpatialBuffer(output, outChannels, input.SampleRate, TargetLayout);
        }
    }

    /// <summary>
    /// Convolution-based room model. One <see cref="Cavern.Filters.FastConvolver"/>
    /// per channel runs the input through a room impulse response. Output is the
    /// dry input mixed with the convolved (wet) signal at <see cref="WetMix"/>.
    ///
    /// The IR is loaded at first call: the loader prefers a real file at
    /// <c>Assets/IR/&lt;room&gt;.wav</c> next to the executable; if that's not
    /// present it falls back to an algorithmically generated room IR. The
    /// algorithmic IRs aren't as nice as real recorded spaces but they sound
    /// like rooms and let the feature ship with no asset downloads.
    /// </summary>
    public sealed class ConvolutionRoomStage : ISpatialStage, System.IDisposable
    {
        public string Name => "Room Convolution";
        public bool Enabled { get; set; } = true;
        public RoomImpulseResponse Room { get; }
        public float WetMix { get; set; } = 0.25f;

        private Cavern.Filters.FastConvolver[]? _convolvers;
        private float[]? _scratch;     // wet buffer, same shape as input
        private float[]? _impulse;     // current IR samples (mono, at pipeline rate)

        private int _cfgChannels;
        private int _cfgSampleRate;
        private int _cfgFrames;

        public ConvolutionRoomStage(RoomImpulseResponse room)
        {
            Room = room;
        }

        public SpatialBuffer Process(SpatialBuffer input)
        {
            if (Room == RoomImpulseResponse.Dry || input.ChannelCount == 0 || input.FrameCount == 0)
                return input;

            EnsureConfigured(input);

            var src = input.Samples;
            var wet = _scratch!;
            System.Array.Copy(src, wet, src.Length);

            // FastConvolver.Process(samples, channel, channels) walks the
            // interleaved buffer and convolves only the requested lane.
            for (int c = 0; c < input.ChannelCount; c++)
                _convolvers![c].Process(wet, c, input.ChannelCount);

            // Mix dry + wet back into a fresh output buffer so we don't mutate
            // the caller's array.
            var output = new float[src.Length];
            float dryGain = 1f - WetMix;
            float wetGain = WetMix;
            for (int i = 0; i < src.Length; i++)
                output[i] = src[i] * dryGain + wet[i] * wetGain;

            return new SpatialBuffer(output, input.ChannelCount, input.SampleRate, input.Layout);
        }

        private void EnsureConfigured(SpatialBuffer input)
        {
            if (_convolvers != null
                && _cfgChannels   == input.ChannelCount
                && _cfgSampleRate == input.SampleRate
                && _cfgFrames     == input.FrameCount)
            {
                return;
            }

            DisposeConvolvers();

            _cfgChannels   = input.ChannelCount;
            _cfgSampleRate = input.SampleRate;
            _cfgFrames     = input.FrameCount;

            _impulse = RoomImpulseLoader.LoadOrSynthesize(Room, input.SampleRate);
            _scratch = new float[input.Samples.Length];

            // FastConvolver has non-thread-safe internal caches. One per channel
            // (each gets its own copy of the IR samples).
            _convolvers = new Cavern.Filters.FastConvolver[input.ChannelCount];
            for (int c = 0; c < input.ChannelCount; c++)
            {
                var irCopy = (float[])_impulse.Clone();
                _convolvers[c] = new Cavern.Filters.FastConvolver(irCopy, input.SampleRate, 0);
            }
        }

        private void DisposeConvolvers()
        {
            if (_convolvers == null) return;
            foreach (var c in _convolvers)
                c?.Dispose();
            _convolvers = null;
        }

        public void Dispose() => DisposeConvolvers();
    }

    /// <summary>
    /// Loads or generates an impulse response for a given room.
    /// Prefers a 32-bit float WAV at <c>Assets/IR/&lt;room&gt;.wav</c> next to
    /// the running executable; otherwise synthesizes an algorithmic IR.
    /// </summary>
    internal static class RoomImpulseLoader
    {
        public static float[] LoadOrSynthesize(RoomImpulseResponse room, int sampleRate)
        {
            var fromDisk = TryLoadWav(room, sampleRate);
            return fromDisk ?? Synthesize(room, sampleRate);
        }

        private static float[]? TryLoadWav(RoomImpulseResponse room, int sampleRate)
        {
            string baseDir = System.AppContext.BaseDirectory;
            string path = System.IO.Path.Combine(baseDir, "Assets", "IR", room + ".wav");
            if (!System.IO.File.Exists(path))
                return null;

            try
            {
                using var reader = new NAudio.Wave.WaveFileReader(path);
                var fmt = reader.WaveFormat;

                // Constraints (kept tight on purpose — anything fancier should
                // be done in an offline IR-prep tool, not at startup):
                //   - Sample rate must match the pipeline's working rate.
                //   - 32-bit IEEE float OR 16-bit PCM only.
                if (fmt.SampleRate != sampleRate) return null;
                bool isFloat = fmt.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32;
                bool isPcm16 = fmt.Encoding == NAudio.Wave.WaveFormatEncoding.Pcm       && fmt.BitsPerSample == 16;
                if (!isFloat && !isPcm16) return null;

                int channels = fmt.Channels;
                int bytesPerSample = fmt.BitsPerSample / 8;
                long totalSamples = reader.Length / bytesPerSample; // across all channels
                var raw = new float[totalSamples];

                var bytes = new byte[reader.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = reader.Read(bytes, read, bytes.Length - read);
                    if (n == 0) break;
                    read += n;
                }

                if (isFloat)
                {
                    System.Buffer.BlockCopy(bytes, 0, raw, 0, read);
                }
                else
                {
                    for (long i = 0; i < totalSamples; i++)
                        raw[i] = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8)) / 32768f;
                }

                if (channels == 1)
                    return raw;

                long frames = totalSamples / channels;
                var mono = new float[frames];
                for (long f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += raw[f * channels + c];
                    mono[f] = sum / channels;
                }
                return mono;
            }
            catch
            {
                // Fall through to synthesis if the file is corrupt or the format
                // is unsupported. Don't crash the audio pipeline over an asset.
                return null;
            }
        }

        // Algorithmic IR: predelay zeros, sparse early reflections, exponentially
        // decaying noise tail. Per-room parameters tune the room "size" feel.
        private static float[] Synthesize(RoomImpulseResponse room, int sampleRate)
        {
            (float lengthSec, float predelayMs, float rt60Sec, int earlyReflections) = room switch
            {
                RoomImpulseResponse.SmallStudio  => (0.40f,  5f, 0.30f, 5),
                RoomImpulseResponse.SmallTheater => (1.00f, 12f, 0.80f, 7),
                RoomImpulseResponse.ConcertHall  => (2.50f, 25f, 2.00f, 9),
                _                                => (0.10f,  0f, 0.05f, 0)
            };

            int len       = System.Math.Max(1, (int)(sampleRate * lengthSec));
            int predelay  = (int)(sampleRate * predelayMs / 1000f);
            float tau     = rt60Sec / 6.908f; // rt60 in seconds -> exp time constant

            var ir = new float[len];
            var rng = new System.Random((int)room * 7919);

            // Early reflections: sparse impulses in the first ~80ms after predelay,
            // amplitudes decreasing from 0.5 down to 0.1.
            int earlyWindow = (int)(sampleRate * 0.080f);
            for (int i = 0; i < earlyReflections; i++)
            {
                int pos = predelay + (int)(rng.NextDouble() * earlyWindow);
                if (pos >= len) break;
                float sign = rng.NextDouble() < 0.5 ? -1f : 1f;
                float amp  = 0.5f * (1f - (float)i / earlyReflections) + 0.1f;
                ir[pos] += sign * amp;
            }

            // Diffuse tail: white noise * exponential decay envelope, starting
            // after the early-reflection window.
            int tailStart = predelay + earlyWindow;
            for (int i = tailStart; i < len; i++)
            {
                float t = (i - tailStart) / (float)sampleRate;
                float env = (float)System.Math.Exp(-t / System.Math.Max(tau, 1e-4f));
                float n = ((float)rng.NextDouble() * 2f - 1f) * env * 0.3f;
                ir[i] += n;
            }

            // Normalize peak to ~0.95 so wet mix is well-behaved across rooms.
            float peak = 0f;
            for (int i = 0; i < len; i++)
            {
                float a = System.Math.Abs(ir[i]);
                if (a > peak) peak = a;
            }
            if (peak > 1e-6f)
            {
                float scale = 0.95f / peak;
                for (int i = 0; i < len; i++) ir[i] *= scale;
            }
            return ir;
        }
    }

    public enum RoomImpulseResponse
    {
        Dry,
        SmallStudio,
        SmallTheater,
        ConcertHall
    }

    /// <summary>
    /// HRTF binaural renderer for headphone targets, backed by Cavern.
    ///
    /// Each input channel is mapped to a positioned <see cref="Cavern.Source"/>
    /// at the standard speaker angle for the layout, the listener's
    /// <see cref="Cavern.Listener.HeadphoneVirtualizer"/> flag folds the scene
    /// to 2-channel binaural, and we copy the rendered samples back into a
    /// fresh interleaved buffer so callers don't share state with Cavern's
    /// internal output array.
    ///
    /// Streaming uses Cavern's <see cref="Cavern.SpecialSources.StreamMaster"/>
    /// pattern: one master fetches a multichannel block per render frame and
    /// fans it out to per-channel <see cref="Cavern.SpecialSources.StreamMasterSource"/>s.
    /// </summary>
    public sealed class CavernBinauralStage : ISpatialStage
    {
        public string Name => "Cavern HRTF";
        public bool Enabled { get; set; } = true;
        public HrtfQuality Quality { get; set; } = HrtfQuality.Default;

        private Cavern.Listener? _listener;
        private Cavern.SpecialSources.StreamMaster? _streamMaster;
        private List<Cavern.Source>? _attachedSources;

        // [channel][frame] holding buffer the StreamMaster getter wraps as a
        // MultichannelWaveform. Reused across calls; reallocated only on
        // layout / frame-count change.
        private float[][]? _pending;

        private int _cfgChannels;
        private int _cfgSampleRate;
        private int _cfgFrames;
        private ChannelLayout _cfgLayout;

        public SpatialBuffer Process(SpatialBuffer input)
        {
            if (input.ChannelCount == 0 || input.FrameCount == 0)
                return input;

            EnsureConfigured(input);

            Deinterleave(input.Samples, _pending!, input.ChannelCount, input.FrameCount);

            float[] rendered = _listener!.Render();

            // Defensive copy: Cavern may reuse internal buffers across frames.
            var output = new float[rendered.Length];
            System.Array.Copy(rendered, output, rendered.Length);

            return new SpatialBuffer(output, 2, input.SampleRate, ChannelLayout.Binaural);
        }

        private void EnsureConfigured(SpatialBuffer input)
        {
            if (_listener != null
                && _cfgChannels   == input.ChannelCount
                && _cfgSampleRate == input.SampleRate
                && _cfgFrames     == input.FrameCount
                && _cfgLayout     == input.Layout)
            {
                return;
            }

            Teardown();

            _cfgChannels   = input.ChannelCount;
            _cfgSampleRate = input.SampleRate;
            _cfgFrames     = input.FrameCount;
            _cfgLayout     = input.Layout;

            _pending = new float[input.ChannelCount][];
            for (int i = 0; i < input.ChannelCount; i++)
                _pending[i] = new float[input.FrameCount];

            _listener = new Cavern.Listener
            {
                SampleRate = input.SampleRate,
                UpdateRate = input.FrameCount
            };

            // HeadphoneVirtualizer is a static toggle on Listener (applies to all
            // listeners on the next render frame).
            Cavern.Listener.HeadphoneVirtualizer = true;

            // Getter returns float[][] sized [channelCount][samplesPerSource].
            // We pre-size _pending to input.FrameCount which equals
            // Listener.UpdateRate, so samplesPerSource will match on every call.
            _streamMaster = new Cavern.SpecialSources.StreamMaster(
                samplesPerSource => _pending!);

            var positions = SpeakerPositions(input.Layout, input.ChannelCount);
            var sources = new List<Cavern.Source>(input.ChannelCount);
            for (int i = 0; i < input.ChannelCount; i++)
            {
                var src = new Cavern.SpecialSources.StreamMasterSource(_streamMaster, i)
                {
                    Position = positions[i]
                };
                sources.Add(src);
                _listener.AttachSource(src);
            }

            // SetupSources' second parameter is the sample rate used by the
            // dummy clip the master attaches to each source.
            _streamMaster.SetupSources(sources, input.SampleRate);
            _attachedSources = sources;
        }

        private void Teardown()
        {
            if (_listener != null && _attachedSources != null)
            {
                foreach (var s in _attachedSources)
                    _listener.DetachSource(s);
            }
            _attachedSources = null;
            _streamMaster    = null;
            _listener        = null;
            _pending         = null;
        }

        private static void Deinterleave(float[] interleaved, float[][] dest, int channels, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                int baseIdx = f * channels;
                for (int c = 0; c < channels; c++)
                    dest[c][f] = interleaved[baseIdx + c];
            }
        }

        // Cavern uses a Unity-like coordinate system: +x right, +y up, +z forward.
        // Azimuth here is measured clockwise from front (0deg = front, +90deg = right).
        // Sources sit at a fixed radius at head height (y=0).
        private static System.Numerics.Vector3[] SpeakerPositions(ChannelLayout layout, int channelCount)
        {
            float[] azDeg = layout switch
            {
                ChannelLayout.Mono         => new[] { 0f },
                ChannelLayout.Stereo       => new[] { -30f, 30f },
                ChannelLayout.Quad         => new[] { -45f, 45f, -135f, 135f },
                ChannelLayout.Surround_5_1 => new[] { -30f, 30f, 0f, 0f, -110f, 110f },
                ChannelLayout.Surround_7_1 => new[] { -30f, 30f, 0f, 0f, -90f, 90f, -135f, 135f },
                _                          => InferEqualSpacing(channelCount)
            };

            if (azDeg.Length != channelCount)
                azDeg = InferEqualSpacing(channelCount);

            const float radius = 10f;
            var positions = new System.Numerics.Vector3[channelCount];
            for (int i = 0; i < channelCount; i++)
            {
                float az = azDeg[i] * (float)System.Math.PI / 180f;
                positions[i] = new System.Numerics.Vector3(
                    radius * (float)System.Math.Sin(az),
                    0f,
                    radius * (float)System.Math.Cos(az));
            }
            return positions;
        }

        private static float[] InferEqualSpacing(int channelCount)
        {
            var a = new float[channelCount];
            for (int i = 0; i < channelCount; i++)
                a[i] = -180f + 360f * i / channelCount;
            return a;
        }
    }

    public enum HrtfQuality
    {
        LowLatency,   // shortest HRIR, lowest CPU, smaller image
        Default,
        High          // longer HRIR, more CPU, broader image
    }
}
