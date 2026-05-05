using System;
using NAudio.Wave;

namespace KhurramAudioRoute.Core.Voice;

/// <summary>
/// Mono Freeverb-style Schroeder reverb. 8 parallel low-passed comb filters
/// build a dense reverb tail, then 4 series all-pass filters smooth the
/// frequency response. Wet / dry mix combines reverberated signal with the
/// dry input. Tuned for voice — see <c>docs/MIC_CHAIN_PLAN.md</c>.
/// </summary>
/// <remarks>
/// Reference: J.A. Moorer / Schroeder reverb topology, with the Freeverb
/// damped-comb-filter improvement. Constants are scaled from the standard
/// 44.1 kHz Freeverb tunings to the source's actual sample rate so the
/// perceived room sounds the same regardless of mic format.
/// </remarks>
public sealed class VoiceReverb : ISampleProvider
{
    // Standard Freeverb comb / allpass tunings at 44.1 kHz.
    private static readonly int[] CombTunings44k = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] AllPassTunings44k = { 556, 441, 341, 225 };

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly CombFilter[] _combs;
    private readonly AllPassFilter[] _allPasses;

    public WaveFormat WaveFormat => _source.WaveFormat;
    public bool Enabled { get; set; } = true;

    /// <summary>0..1. Higher = longer tail (comb feedback approaches 1).</summary>
    public float RoomSize
    {
        get => _roomSize;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(_roomSize - clamped) < 0.0005f) return;
            _roomSize = clamped;
            float feedback = 0.7f + 0.28f * clamped; // Freeverb's "scaleroom" mapping.
            for (int i = 0; i < _combs.Length; i++) _combs[i].Feedback = feedback;
        }
    }
    private float _roomSize;

    /// <summary>0..1. Higher = darker tail (high frequencies decay faster).</summary>
    public float Damping
    {
        get => _damping;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(_damping - clamped) < 0.0005f) return;
            _damping = clamped;
            float damp = clamped * 0.4f;
            for (int i = 0; i < _combs.Length; i++) _combs[i].Damping = damp;
        }
    }
    private float _damping;

    /// <summary>0..1. 0 = dry only, 1 = wet only. Voice usually wants 0.10–0.35.</summary>
    public float WetMix { get; set; }

    public VoiceReverb(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("VoiceReverb requires IEEE float input.", nameof(source));
        _channels = source.WaveFormat.Channels;

        int sr = source.WaveFormat.SampleRate;
        float scale = sr / 44100f;
        _combs = new CombFilter[CombTunings44k.Length];
        for (int i = 0; i < _combs.Length; i++)
            _combs[i] = new CombFilter((int)Math.Round(CombTunings44k[i] * scale));
        _allPasses = new AllPassFilter[AllPassTunings44k.Length];
        for (int i = 0; i < _allPasses.Length; i++)
            _allPasses[i] = new AllPassFilter((int)Math.Round(AllPassTunings44k[i] * scale));

        RoomSize = 0.5f;
        Damping = 0.4f;
        WetMix = 0f;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (!Enabled || WetMix <= 0.001f || read == 0) return read;

        float wet = WetMix;
        float dry = 1f - wet;
        int frames = read / _channels;

        for (int frame = 0; frame < frames; frame++)
        {
            int i = offset + frame * _channels;

            // Mono detector — average channels — feed the reverb network.
            float input = 0f;
            for (int c = 0; c < _channels; c++) input += buffer[i + c];
            input /= _channels;

            float reverb = 0f;
            for (int k = 0; k < _combs.Length; k++) reverb += _combs[k].Process(input);
            reverb *= 1f / _combs.Length;
            for (int k = 0; k < _allPasses.Length; k++) reverb = _allPasses[k].Process(reverb);

            for (int c = 0; c < _channels; c++)
                buffer[i + c] = buffer[i + c] * dry + reverb * wet;
        }

        return read;
    }

    private sealed class CombFilter
    {
        private readonly float[] _buffer;
        private int _index;
        private float _filterStore;

        public float Feedback { get; set; }
        public float Damping { get; set; }

        public CombFilter(int delaySamples)
        {
            _buffer = new float[Math.Max(1, delaySamples)];
        }

        public float Process(float input)
        {
            float output = _buffer[_index];
            _filterStore = output * (1f - Damping) + _filterStore * Damping;
            _buffer[_index] = input + _filterStore * Feedback;
            _index++;
            if (_index >= _buffer.Length) _index = 0;
            return output;
        }
    }

    private sealed class AllPassFilter
    {
        private const float Gain = 0.5f;
        private readonly float[] _buffer;
        private int _index;

        public AllPassFilter(int delaySamples)
        {
            _buffer = new float[Math.Max(1, delaySamples)];
        }

        public float Process(float input)
        {
            float buffered = _buffer[_index];
            float output = -input + buffered;
            _buffer[_index] = input + buffered * Gain;
            _index++;
            if (_index >= _buffer.Length) _index = 0;
            return output;
        }
    }
}
