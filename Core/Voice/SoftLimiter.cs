using System;
using NAudio.Wave;

namespace KhurramAudioRoute.Core.Voice;

/// <summary>
/// Soft limiter for the voice chain. Mirrors the spatial pipeline's
/// <c>SpatialMath.Limit</c> shape (clamp ±1.4, then tanh) but kept here so the
/// voice namespace doesn't depend on spatial internals. See
/// <c>docs/MIC_CHAIN_PLAN.md</c>.
/// </summary>
public sealed class SoftLimiter : ISampleProvider
{
    private readonly ISampleProvider _source;
    public WaveFormat WaveFormat => _source.WaveFormat;
    public bool Enabled { get; set; } = true;

    public SoftLimiter(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (!Enabled || read == 0) return read;

        int end = offset + read;
        for (int i = offset; i < end; i++)
        {
            float v = buffer[i];
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                buffer[i] = 0f;
                continue;
            }
            if (v > 1.4f) v = 1.4f;
            else if (v < -1.4f) v = -1.4f;
            buffer[i] = (float)Math.Tanh(v);
        }
        return read;
    }
}
