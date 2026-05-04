using System;
using NAudio.Wave;

namespace KhurramAudioRoute.Core.Voice;

/// <summary>
/// Mono dynamics compressor. Peak-detection envelope follower with separate
/// attack and release time constants; soft-knee curve around the threshold;
/// linear makeup gain. Designed for the voice chain — see
/// <c>docs/MIC_CHAIN_PLAN.md</c>.
/// </summary>
/// <remarks>
/// Standard topology: detect peak envelope, compute over-threshold amount in
/// dB, multiply by (1 - 1/ratio) for the gain reduction, smooth with the
/// release coefficient (attack on rising envelope), convert back to linear,
/// multiply through with makeup. No look-ahead.
/// </remarks>
public sealed class Compressor : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sampleRate;
    private readonly int _channels;

    private float _envelope;
    private float _gainReductionDb;

    public WaveFormat WaveFormat => _source.WaveFormat;
    public bool Enabled { get; set; } = true;

    /// <summary>Threshold in dBFS where compression begins. -18 is a sensible voice default.</summary>
    public float ThresholdDb { get; set; } = -18f;

    /// <summary>Ratio &gt;= 1. 1 = no compression; 4 = 4:1; 20+ ≈ limiting.</summary>
    public float Ratio { get; set; } = 3f;

    /// <summary>Soft-knee width in dB centred on threshold. 6 = subtle, 0 = hard knee.</summary>
    public float KneeDb { get; set; } = 6f;

    /// <summary>Envelope attack time in ms.</summary>
    public float AttackMs { get; set; } = 5f;

    /// <summary>Envelope release time in ms.</summary>
    public float ReleaseMs { get; set; } = 80f;

    /// <summary>Makeup gain applied after compression, in dB.</summary>
    public float MakeupDb { get; set; } = 4f;

    /// <summary>Last gain-reduction value (negative number, in dB) — exposed for UI metering.</summary>
    public float CurrentGainReductionDb => _gainReductionDb;

    public Compressor(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("Compressor requires IEEE float input.", nameof(source));
        _sampleRate = source.WaveFormat.SampleRate;
        _channels = source.WaveFormat.Channels;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (!Enabled || read == 0) return read;

        float attackCoeff = Coeff(AttackMs);
        float releaseCoeff = Coeff(ReleaseMs);
        float makeup = (float)Math.Pow(10.0, MakeupDb / 20.0);
        float threshold = ThresholdDb;
        float ratio = Math.Max(1f, Ratio);
        float ratioInv = 1f - 1f / ratio;
        float halfKnee = Math.Max(0f, KneeDb) * 0.5f;
        int frames = read / _channels;

        for (int frame = 0; frame < frames; frame++)
        {
            int i = offset + frame * _channels;

            float peak = 0f;
            for (int c = 0; c < _channels; c++)
            {
                float a = buffer[i + c];
                if (a < 0f) a = -a;
                if (a > peak) peak = a;
            }

            float coeff = peak > _envelope ? attackCoeff : releaseCoeff;
            _envelope += (peak - _envelope) * coeff;

            float envelopeDb = _envelope <= 1e-7f ? -140f : (float)(20.0 * Math.Log10(_envelope));
            float over = envelopeDb - threshold;

            float reductionDb;
            if (over <= -halfKnee)
            {
                reductionDb = 0f;
            }
            else if (over >= halfKnee)
            {
                reductionDb = -(over * ratioInv);
            }
            else
            {
                // Quadratic soft knee bridge between 0 and full ratio.
                float kneePos = (over + halfKnee);
                float kneeAmt = (kneePos * kneePos) / (4f * halfKnee);
                reductionDb = -(kneeAmt * ratioInv);
            }

            _gainReductionDb = reductionDb;
            float gain = (float)Math.Pow(10.0, reductionDb / 20.0) * makeup;

            for (int c = 0; c < _channels; c++)
                buffer[i + c] *= gain;
        }

        return read;
    }

    private float Coeff(float ms)
    {
        if (ms <= 0f) return 1f;
        return 1f - (float)Math.Exp(-1.0 / (ms * 0.001 * _sampleRate));
    }
}
