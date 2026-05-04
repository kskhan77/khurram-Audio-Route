using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KhurramAudioRoute.Core.SyncCalibration.L3;

/// <summary>
/// Records a short window from the system default capture endpoint into a
/// mono IEEE-float buffer at the device's native sample rate.
/// </summary>
/// <remarks>
/// L3 mic-based auto-sync expects the captured signal at the same sample
/// rate as the reference sweep. Callers should regenerate the sweep at
/// <see cref="SampleRate"/> after capture starts (or feed an upsampled
/// reference into the cross-correlator).
/// </remarks>
public sealed class MicCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly List<float> _samples = new();
    private WasapiCapture? _capture;
    private bool _disposed;

    public int SampleRate { get; private set; }
    public int ChannelCount { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>Resolve and open the default capture endpoint without starting.</summary>
    public static bool TryGetDefaultMicId(out string deviceId, out string? friendlyName)
    {
        deviceId = "";
        friendlyName = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            deviceId = dev.ID;
            friendlyName = dev.FriendlyName;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Start(string? deviceId = null, int targetCapacityHintSamples = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) throw new InvalidOperationException("MicCapture already running.");

        using var enumerator = new MMDeviceEnumerator();
        MMDevice mm = string.IsNullOrWhiteSpace(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            : enumerator.GetDevice(deviceId);

        try
        {
            _capture = new WasapiCapture(mm, useEventSync: true, audioBufferMillisecondsLength: 50);
            SampleRate = _capture.WaveFormat.SampleRate;
            ChannelCount = _capture.WaveFormat.Channels;

            lock (_gate)
            {
                _samples.Clear();
                if (targetCapacityHintSamples > 0)
                    _samples.Capacity = targetCapacityHintSamples;
            }

            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
            IsRunning = true;
        }
        finally
        {
            mm.Dispose();
        }
    }

    public void Stop()
    {
        if (!IsRunning || _capture is null) return;
        try { _capture.StopRecording(); } catch { }
        try { _capture.DataAvailable -= OnDataAvailable; } catch { }
        try { _capture.Dispose(); } catch { }
        _capture = null;
        IsRunning = false;
    }

    /// <summary>Snapshot of the mono down-mixed buffer captured so far.</summary>
    public float[] Snapshot()
    {
        lock (_gate)
            return _samples.ToArray();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture is null || e.BytesRecorded <= 0) return;
        var fmt = _capture.WaveFormat;

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            int samples = e.BytesRecorded / 4;
            int frames = samples / fmt.Channels;
            lock (_gate)
            {
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0f;
                    int frameOfs = f * fmt.Channels * 4;
                    for (int c = 0; c < fmt.Channels; c++)
                    {
                        int o = frameOfs + c * 4;
                        sum += BitConverter.ToSingle(e.Buffer, o);
                    }
                    _samples.Add(sum / fmt.Channels);
                }
            }
        }
        else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            int frames = e.BytesRecorded / (fmt.Channels * 2);
            const float scale = 1f / 32768f;
            lock (_gate)
            {
                for (int f = 0; f < frames; f++)
                {
                    int frameOfs = f * fmt.Channels * 2;
                    int sum = 0;
                    for (int c = 0; c < fmt.Channels; c++)
                    {
                        int o = frameOfs + c * 2;
                        short s = (short)(e.Buffer[o] | (e.Buffer[o + 1] << 8));
                        sum += s;
                    }
                    _samples.Add(sum * scale / fmt.Channels);
                }
            }
        }
        // Other PCM widths are uncommon on default capture endpoints; if we
        // hit one in the wild we'll add a branch then. Bailing silently keeps
        // the runner from crashing — xcorr will simply not find a peak.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
