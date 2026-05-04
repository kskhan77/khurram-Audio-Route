using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KhurramAudioRoute.Core.SyncCalibration.L3;

/// <summary>
/// Plays a known mono float sweep to a specific render endpoint via WASAPI
/// shared mode. Mirrors <see cref="SyncCalibration.SyncClickPlayer"/> in
/// shape so L2 / L3 share the same audio plumbing model.
/// </summary>
public static class SweepPlayer
{
    public const int SharedWasapiLatencyMs = 45;

    public static void PlayBlocking(string deviceId, float[] sweepMono, int sampleRate)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || sweepMono is null || sweepMono.Length == 0)
            return;

        WaveFormat fmt = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        byte[] pcm = new byte[sweepMono.Length * 4];
        Buffer.BlockCopy(sweepMono, 0, pcm, 0, pcm.Length);

        using var enumerator = new MMDeviceEnumerator();
        using MMDevice mm = enumerator.GetDevice(deviceId);
        BufferedWaveProvider bwp = new(fmt)
        {
            DiscardOnBufferOverflow = true,
            BufferLength = pcm.Length + 65536,
        };
        bwp.AddSamples(pcm, 0, pcm.Length);

        using WasapiOut output = new(mm, AudioClientShareMode.Shared, true, SharedWasapiLatencyMs);
        output.Init(bwp);

        using var wait = new ManualResetEventSlim(false);
        void OnStopped(object? _, StoppedEventArgs __) => wait.Set();
        output.PlaybackStopped += OnStopped;
        try
        {
            output.Play();
            wait.Wait(TimeSpan.FromSeconds(5));
        }
        finally
        {
            output.PlaybackStopped -= OnStopped;
            try { output.Stop(); } catch { }
        }
    }
}
