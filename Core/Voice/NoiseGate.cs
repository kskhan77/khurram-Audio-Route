using System;
using NAudio.Wave;

namespace KhurramAudioRoute.Core.Voice;

/// <summary>
/// Simple amplitude-following noise gate (mic chain stage 1).
/// See <c>docs/MIC_CHAIN_PLAN.md</c>.
/// </summary>
/// <remarks>
/// Tracks the per-sample peak envelope with attack/release smoothing.
/// While the envelope is below the open threshold, a hold timer counts
/// down; once it expires the gate ramps the gain to zero over the close
/// time. When the envelope crosses the open threshold the gain ramps
/// back to unity over the open time. No look-ahead, no allocations on
/// the hot path.
/// </remarks>
public sealed class NoiseGate : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _sampleRate;

    private float _envelope;
    private float _gain;
    private float _holdSamplesLeft;
    private bool _isOpen;

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Open-gate threshold in linear amplitude (0..1). Default = -40 dBFS.</summary>
    public float OpenThreshold { get; set; } = LinearFromDb(-40f);

    /// <summary>Close-gate threshold in linear amplitude. Should be ≤ OpenThreshold for hysteresis.</summary>
    public float CloseThreshold { get; set; } = LinearFromDb(-46f);

    /// <summary>How long the gate stays open after the signal drops below close threshold.</summary>
    public float HoldMs { get; set; } = 80f;

    /// <summary>Open ramp time once the signal crosses OpenThreshold.</summary>
    public float OpenMs { get; set; } = 5f;

    /// <summary>Close ramp time after Hold expires.</summary>
    public float CloseMs { get; set; } = 80f;

    /// <summary>Envelope follower attack (rise) time.</summary>
    public float EnvelopeAttackMs { get; set; } = 1f;

    /// <summary>Envelope follower release (decay) time.</summary>
    public float EnvelopeReleaseMs { get; set; } = 50f;

    public bool Enabled { get; set; } = true;

    public NoiseGate(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("NoiseGate requires IEEE float input.", nameof(source));
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (!Enabled || read == 0) return read;

        float envAttack = TimeToCoeff(EnvelopeAttackMs);
        float envRelease = TimeToCoeff(EnvelopeReleaseMs);
        float openStep = TimeToStep(OpenMs);
        float closeStep = TimeToStep(CloseMs);
        int frames = read / _channels;

        for (int frame = 0; frame < frames; frame++)
        {
            int i = offset + frame * _channels;

            float peak = 0f;
            for (int c = 0; c < _channels; c++)
            {
                float a = Math.Abs(buffer[i + c]);
                if (a > peak) peak = a;
            }

            float coeff = peak > _envelope ? envAttack : envRelease;
            _envelope += (peak - _envelope) * coeff;

            if (_envelope >= OpenThreshold)
            {
                _isOpen = true;
                _holdSamplesLeft = HoldMs * _sampleRate / 1000f;
            }
            else if (_envelope < CloseThreshold)
            {
                if (_holdSamplesLeft > 0f) _holdSamplesLeft -= 1f;
                else _isOpen = false;
            }

            float target = _isOpen ? 1f : 0f;
            if (_gain < target)
            {
                _gain += openStep;
                if (_gain > target) _gain = target;
            }
            else if (_gain > target)
            {
                _gain -= closeStep;
                if (_gain < target) _gain = target;
            }

            for (int c = 0; c < _channels; c++)
                buffer[i + c] *= _gain;
        }

        return read;
    }

    private float TimeToCoeff(float ms)
    {
        if (ms <= 0f) return 1f;
        // Standard 1-pole envelope: y += (x - y) * coeff. Solving for ~63% rise in `ms`.
        return 1f - (float)Math.Exp(-1.0 / (ms * 0.001 * _sampleRate));
    }

    private float TimeToStep(float ms)
    {
        if (ms <= 0f) return 1f;
        // Linear ramp from 0 to 1 over `ms`.
        return 1000f / (ms * _sampleRate);
    }

    public static float LinearFromDb(float db) => (float)Math.Pow(10.0, db / 20.0);
    public static float DbFromLinear(float lin) => lin <= 0f ? -120f : (float)(20.0 * Math.Log10(lin));
}
