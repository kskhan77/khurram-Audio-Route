using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace KhurramAudioRoute.Core.Voice;

/// <summary>
/// De-esser for the voice chain. Side-chain detection band-pass biquad isolates
/// sibilance (~6.5 kHz). When the detection envelope crosses the threshold the
/// stage applies a dynamic high-shelf cut to the main signal, scaled by how
/// far over threshold we are. See <c>docs/MIC_CHAIN_PLAN.md</c>.
/// </summary>
public sealed class DeEsser : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sampleRate;
    private readonly int _channels;

    private BiQuadFilter _detect;
    private BiQuadFilter[] _shelves;
    private float _detectFreq;
    private float _envelope;
    private float _gainReductionDb;

    public WaveFormat WaveFormat => _source.WaveFormat;
    public bool Enabled { get; set; } = true;

    /// <summary>Detection threshold in dBFS for the side-chain band-pass envelope.</summary>
    public float ThresholdDb { get; set; } = -22f;

    /// <summary>Maximum high-shelf cut applied at peak sibilance (negative dB; e.g. -8).</summary>
    public float MaxReductionDb { get; set; } = -8f;

    /// <summary>Centre frequency of the detection band-pass and shelf cut.</summary>
    public float Frequency
    {
        get => _detectFreq;
        set
        {
            float clamped = Math.Clamp(value, 1000f, 12000f);
            if (Math.Abs(_detectFreq - clamped) < 1f) return;
            _detectFreq = clamped;
            RebuildFilters();
        }
    }

    public float AttackMs { get; set; } = 2f;
    public float ReleaseMs { get; set; } = 40f;

    /// <summary>Last applied high-shelf reduction (negative dB) for UI metering.</summary>
    public float CurrentReductionDb => _gainReductionDb;

    public DeEsser(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("DeEsser requires IEEE float input.", nameof(source));
        _sampleRate = source.WaveFormat.SampleRate;
        _channels = source.WaveFormat.Channels;
        _detectFreq = 6500f;
        _detect = BiQuadFilter.PeakingEQ(_sampleRate, _detectFreq, 2.0f, 0f);
        _shelves = Array.Empty<BiQuadFilter>();
        RebuildFilters();
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (!Enabled || read == 0) return read;
        if (_shelves.Length != _channels) RebuildFilters();

        float attackCoeff = Coeff(AttackMs);
        float releaseCoeff = Coeff(ReleaseMs);
        float threshold = (float)Math.Pow(10.0, ThresholdDb / 20.0);
        float maxReductionDb = MaxReductionDb;
        int frames = read / _channels;

        for (int frame = 0; frame < frames; frame++)
        {
            int i = offset + frame * _channels;

            // Side-chain: isolate sibilance band (mono detector — average channels).
            float mono = 0f;
            for (int c = 0; c < _channels; c++) mono += buffer[i + c];
            mono /= _channels;
            float sib = _detect.Transform(mono);
            float a = sib < 0f ? -sib : sib;

            float coeff = a > _envelope ? attackCoeff : releaseCoeff;
            _envelope += (a - _envelope) * coeff;

            // Reduction proportional to over-threshold ratio in linear amplitude.
            float reductionDb = 0f;
            if (_envelope > threshold)
            {
                float over = _envelope - threshold;
                float overNorm = over / Math.Max(threshold, 1e-6f);
                if (overNorm > 1f) overNorm = 1f;
                reductionDb = maxReductionDb * overNorm;
            }
            _gainReductionDb = reductionDb;

            // Rebuild high-shelf with the time-varying gain. Dirty trick — building
            // a biquad per sample is expensive but at 48 k it's still well under the
            // hot-path budget for a mono voice chain.
            for (int c = 0; c < _channels; c++)
                _shelves[c] = BiQuadFilter.HighShelf(_sampleRate, _detectFreq, 0.7f, reductionDb);

            for (int c = 0; c < _channels; c++)
                buffer[i + c] = _shelves[c].Transform(buffer[i + c]);
        }

        return read;
    }

    private void RebuildFilters()
    {
        _detect = BiQuadFilter.PeakingEQ(_sampleRate, _detectFreq, 2.0f, 0f);
        _shelves = new BiQuadFilter[_channels];
        for (int c = 0; c < _channels; c++)
            _shelves[c] = BiQuadFilter.HighShelf(_sampleRate, _detectFreq, 0.7f, 0f);
    }

    private float Coeff(float ms)
    {
        if (ms <= 0f) return 1f;
        return 1f - (float)Math.Exp(-1.0 / (ms * 0.001 * _sampleRate));
    }
}
