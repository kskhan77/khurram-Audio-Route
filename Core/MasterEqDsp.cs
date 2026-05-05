using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NAudio.Dsp;

namespace KhurramAudioRoute.Core;

/// <summary>
/// 10-band ISO peaking EQ for the master bridge. Designed to be invoked
/// <b>inline</b> inside each per-target WASAPI procedure, right after
/// <c>Bass.ChannelGetData</c>, on the byte buffer that WASAPI hands back to
/// Windows. This is the only attachment point that empirically affects the
/// audio you hear — BASSmix splits expose pre-DSP source data, so
/// <c>Bass.ChannelSetDSP</c> on the master mixer / push stream / split is
/// silently a no-op even though the callback runs.
/// </summary>
/// <remarks>
/// One <see cref="MasterEqDsp"/> instance per bridge run. Register each
/// per-target split with its negotiated format via
/// <see cref="RegisterTarget"/> when the WASAPI session is opened. The proc
/// then calls <see cref="ProcessInline(int, IntPtr, int)"/> with the buffer
/// and byte count returned by <c>Bass.ChannelGetData</c>. Filter coefficients
/// are shared via <see cref="UpdateGains"/>; per-target sample state lives in
/// <see cref="TargetState.Filters"/>.
/// </remarks>
public sealed class MasterEqDsp : IDisposable
{
    /// <summary>Q value used for every PeakingEQ band. ~1.4 gives a ~1-octave bell.</summary>
    public const float BandQ = 1.4f;

    private readonly object _lock = new();
    private readonly Dictionary<int, TargetState> _targets = new();
    private readonly float[] _gains = new float[MasterEngine.IsoCenterFrequencies.Length];
    private bool _disposed;

    private sealed class TargetState
    {
        public int Channels;
        public int SampleRate;
        public BiQuadFilter[][] Filters = Array.Empty<BiQuadFilter[]>();
        public long CallbackCount;
        public long FrameCount;
    }

    /// <summary>
    /// Diagnostic kill switch. When != 1.0, every sample is multiplied by this
    /// before the biquads run. 0 = silence, 0.5 = -6 dB. If audibly affects
    /// output, our processing is on the live audio path. If it doesn't, the
    /// hardware is hearing audio from somewhere we're not touching.
    /// </summary>
    public float TestKillFactor { get; set; } = 1.0f;

    public MasterEqDsp(float[]? initialGains)
    {
        for (int b = 0; b < _gains.Length; b++)
            _gains[b] = b < (initialGains?.Length ?? 0) ? initialGains![b] : 0f;
    }

    /// <summary>
    /// Register a per-target split for inline processing. Channel count + sample
    /// rate must match what the WASAPI proc reads from this split.
    /// </summary>
    public bool RegisterTarget(int splitHandle, int channels, int sampleRate)
    {
        if (splitHandle == 0 || _disposed) return false;
        if (channels <= 0 || sampleRate <= 0) return false;

        var state = new TargetState
        {
            Channels = channels,
            SampleRate = sampleRate,
            Filters = BuildFilters(channels, sampleRate, _gains),
        };
        lock (_lock) _targets[splitHandle] = state;
        Debug.WriteLine($"MasterEqDsp.RegisterTarget: split={splitHandle}, channels={channels}, sr={sampleRate}");
        return true;
    }

    public void UnregisterTarget(int splitHandle)
    {
        if (_disposed) return;
        lock (_lock) _targets.Remove(splitHandle);
    }

    /// <summary>Update the shared gain state and rebuild every target's filter chain.</summary>
    public void UpdateGains(float[] gains)
    {
        if (gains is null || _disposed) return;
        lock (_lock)
        {
            for (int b = 0; b < _gains.Length; b++)
                _gains[b] = b < gains.Length ? gains[b] : 0f;
            foreach (var kvp in _targets)
            {
                var s = kvp.Value;
                s.Filters = BuildFilters(s.Channels, s.SampleRate, _gains);
            }
        }
    }

    /// <summary>
    /// Process IEEE-float interleaved samples in-place. Call this from the
    /// WASAPI procedure with the buffer + byte count returned by
    /// <c>Bass.ChannelGetData(split, buf, len)</c>.
    /// </summary>
    public void ProcessInline(int splitHandle, IntPtr buffer, int byteLength)
    {
        if (byteLength <= 0 || _disposed) return;

        TargetState? s;
        lock (_lock)
        {
            if (!_targets.TryGetValue(splitHandle, out s))
                return;
        }

        int sampleCount = byteLength / sizeof(float);
        int channels = s.Channels;
        if (channels <= 0) return;
        int frames = sampleCount / channels;
        var filters = s.Filters;
        if (filters.Length != channels) return;

        Interlocked.Increment(ref s.CallbackCount);
        Interlocked.Add(ref s.FrameCount, frames);

        float kill = TestKillFactor;

        unsafe
        {
            float* data = (float*)buffer.ToPointer();
            for (int f = 0; f < frames; f++)
            {
                int i = f * channels;
                for (int c = 0; c < channels; c++)
                {
                    float v = data[i + c] * kill;
                    var bands = filters[c];
                    for (int b = 0; b < bands.Length; b++)
                        v = bands[b].Transform(v);
                    data[i + c] = v;
                }
            }
        }
    }

    public int RegisteredTargetCount
    {
        get { lock (_lock) return _targets.Count; }
    }

    public long TotalCallbackCount
    {
        get
        {
            long sum = 0;
            lock (_lock)
                foreach (var s in _targets.Values) sum += Interlocked.Read(ref s.CallbackCount);
            return sum;
        }
    }

    public long TotalFrameCount
    {
        get
        {
            long sum = 0;
            lock (_lock)
                foreach (var s in _targets.Values) sum += Interlocked.Read(ref s.FrameCount);
            return sum;
        }
    }

    /// <summary>For diagnostics: list of (splitHandle, callbacks, frames) per target.</summary>
    public IReadOnlyList<(int Split, long Callbacks, long Frames)> AttachmentStats()
    {
        var list = new List<(int, long, long)>();
        lock (_lock)
        {
            foreach (var kvp in _targets)
                list.Add((kvp.Key,
                    Interlocked.Read(ref kvp.Value.CallbackCount),
                    Interlocked.Read(ref kvp.Value.FrameCount)));
        }
        return list;
    }

    /// <summary>Backwards-compatible alias used by the bridge orchestrator.</summary>
    public int AttachedCount => RegisteredTargetCount;

    private static BiQuadFilter[][] BuildFilters(int channels, int sampleRate, float[] gains)
    {
        var freqs = MasterEngine.IsoCenterFrequencies;
        var f = new BiQuadFilter[channels][];
        for (int c = 0; c < channels; c++)
        {
            f[c] = new BiQuadFilter[freqs.Length];
            for (int b = 0; b < freqs.Length; b++)
            {
                float g = b < gains.Length ? gains[b] : 0f;
                f[c][b] = BiQuadFilter.PeakingEQ(sampleRate, freqs[b], BandQ, g);
            }
        }
        return f;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock) _targets.Clear();
    }
}
