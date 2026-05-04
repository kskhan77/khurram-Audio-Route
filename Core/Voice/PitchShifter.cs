using System;
using NAudio.Wave;
using SoundTouch;

namespace KhurramAudioRoute.Core.Voice;

/// <summary>
/// Real-time pitch shifter for the voice chain. Wraps
/// <see cref="SoundTouchProcessor"/> as an <see cref="ISampleProvider"/> so it
/// composes with the rest of the mic chain. Tempo is preserved (only pitch
/// changes); a value of 0 semitones short-circuits SoundTouch entirely so
/// there's no startup latency or quality cost when bypassed.
/// See <c>docs/MIC_CHAIN_PLAN.md</c>.
/// </summary>
public sealed class PitchShifter : ISampleProvider
{
    public const float MinSemitones = -12f;
    public const float MaxSemitones = 12f;
    private const float BypassThreshold = 0.05f;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly SoundTouchProcessor _stretch;
    private float[] _scratch = Array.Empty<float>();
    private float _semitones;

    public WaveFormat WaveFormat => _source.WaveFormat;
    public bool Enabled { get; set; } = true;

    public float Semitones
    {
        get => _semitones;
        set
        {
            float clamped = value;
            if (clamped < MinSemitones) clamped = MinSemitones;
            else if (clamped > MaxSemitones) clamped = MaxSemitones;
            if (Math.Abs(_semitones - clamped) < 0.001f) return;
            _semitones = clamped;
            _stretch.PitchSemiTones = clamped;
        }
    }

    public PitchShifter(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("PitchShifter requires IEEE float input.", nameof(source));

        _channels = source.WaveFormat.Channels;
        _stretch = new SoundTouchProcessor
        {
            SampleRate = source.WaveFormat.SampleRate,
            Channels = _channels,
        };
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // Bypass: forward without paying for SoundTouch's startup latency.
        if (!Enabled || Math.Abs(_semitones) < BypassThreshold)
            return _source.Read(buffer, offset, count);

        if (_scratch.Length < count) _scratch = new float[count];

        int read = _source.Read(_scratch, 0, count);
        if (read <= 0) return 0;

        int frames = read / _channels;
        ReadOnlySpan<float> input = _scratch.AsSpan(0, read);
        _stretch.PutSamples(input, frames);

        Span<float> outSpan = buffer.AsSpan(offset, count);
        int framesOut = _stretch.ReceiveSamples(outSpan, count / _channels);
        int samplesOut = framesOut * _channels;

        // SoundTouch holds back a few frames during startup. Pad the rest of the
        // request with silence so downstream consumers (resampler, WasapiOut) get
        // the count they asked for and the audio thread doesn't stall.
        if (samplesOut < count)
            outSpan.Slice(samplesOut, count - samplesOut).Clear();

        return count;
    }

    public void Reset()
    {
        try { _stretch.Clear(); } catch { }
    }
}
