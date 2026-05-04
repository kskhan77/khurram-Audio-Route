using System;
using NAudio.Wave;

namespace KhurramAudioRoute.Core.Voice;

/// <summary>
/// Transparent <see cref="ISampleProvider"/> that observes the absolute peak of
/// every read and exposes it through <see cref="Peak"/>. Used by the Voice
/// Studio card to drive the post-chain level meter. Mirrors the input peak
/// decay used in <see cref="MicChainEngine"/> so both bars feel symmetric.
/// </summary>
public sealed class PeakMonitor : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float _peak;

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Last observed peak with frame-to-frame decay (0..1).</summary>
    public float Peak => _peak;

    /// <summary>Decay factor applied each Read call. Default 0.85.</summary>
    public float Decay { get; set; } = 0.85f;

    public PeakMonitor(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read == 0)
        {
            _peak *= Decay;
            return 0;
        }

        float framePeak = 0f;
        int end = offset + read;
        for (int i = offset; i < end; i++)
        {
            float v = buffer[i];
            float a = v < 0f ? -v : v;
            if (a > framePeak) framePeak = a;
        }

        float held = _peak * Decay;
        _peak = framePeak > held ? framePeak : held;
        return read;
    }

    public void Reset() => _peak = 0f;
}
