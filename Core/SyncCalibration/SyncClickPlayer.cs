using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KhurramAudioRoute.Core.SyncCalibration;

/// <summary>
/// Impulsive clicks on render endpoints via WASAPI (outside the mirror bus).
/// L2 perceptual sync — see <c>docs/LATENCY_PLAN.md</c>.
/// </summary>
public static class SyncClickPlayer
{
    public const int SharedWasapiLatencyMs = 45;
    public const int InterBurstGapMs = 260;
    public const int TailSilenceMs = 160;

    private static byte[] RenderClickBurstPcm16Stereo(int sampleRate)
    {
        const int BurstMs = 48;
        int frameCount = sampleRate * BurstMs / 1000;
        frameCount = Math.Max(frameCount, 16);
        byte[] pcm = new byte[frameCount * 4];
        double freq = 1850;

        int last = Math.Max(frameCount - 1, 1);
        for (int frame = 0; frame < frameCount; frame++)
        {
            double t = frame / (double)sampleRate;
            double hann = Math.Sin(Math.PI * frame / last); // tapered burst
            double sine = Math.Sin(2 * Math.PI * freq * t);
            double sample = sine * hann * short.MaxValue * 0.42;
            short s16 = unchecked((short)Math.Clamp(sample, short.MinValue, short.MaxValue));
            int ofs = frame * 4;
            pcm[ofs] = (byte)(s16 & 0xFF);
            pcm[ofs + 1] = (byte)((s16 >> 8) & 0xFF);
            pcm[ofs + 2] = pcm[ofs];
            pcm[ofs + 3] = pcm[ofs + 1];
        }

        return pcm;
    }

    public static bool TryCaptureFingerprint(string deviceId, out string fingerprint)
    {
        fingerprint = "";
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        try
        {
            WaveFormat pcmFormat = new(48000, 16, 2);
            byte[] pcm = RenderClickBurstPcm16Stereo(48000);

            using var enumerator = new MMDeviceEnumerator();
            using MMDevice mm = enumerator.GetDevice(deviceId);
            BufferedWaveProvider bwp = new(pcmFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferLength = pcm.Length + 49152,
            };
            using WasapiOut output = BuildOutput(mm);
            bwp.AddSamples(pcm, 0, pcm.Length);
            output.Init(bwp);
            var wf = output.OutputWaveFormat;
            fingerprint = $"{wf.SampleRate}_{wf.Channels}_{wf.BitsPerSample}";
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SyncCalibration fingerprint failed: {ex.Message}");
            return false;
        }
    }

    public static Task PlaySingleClickAsync(string deviceId, CancellationToken ct)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            PlaySingleClickBlocking(deviceId);
        }, ct);

    /// <summary>Reference — DUT — reference bookends.</summary>
    public static async Task PlayRefMiddleRefAsync(string referenceDeviceId, string dutDeviceId, CancellationToken ct)
    {
        await PlaySingleClickAsync(referenceDeviceId, ct).ConfigureAwait(true);
        await Task.Delay(InterBurstGapMs, ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        await PlaySingleClickAsync(dutDeviceId, ct).ConfigureAwait(true);
        await Task.Delay(InterBurstGapMs, ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        await PlaySingleClickAsync(referenceDeviceId, ct).ConfigureAwait(true);
        await Task.Delay(TailSilenceMs, ct).ConfigureAwait(true);
    }

    private static void PlaySingleClickBlocking(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        WaveFormat pcmFormat = new(48000, 16, 2);
        byte[] pcm = RenderClickBurstPcm16Stereo(48000);

        using var enumerator = new MMDeviceEnumerator();
        using MMDevice mm = enumerator.GetDevice(deviceId);
        BufferedWaveProvider bwp = new(pcmFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferLength = pcm.Length + 49152,
        };

        using WasapiOut output = BuildOutput(mm);
        bwp.AddSamples(pcm, 0, pcm.Length);
        output.Init(bwp);

        using var wait = new ManualResetEventSlim(false);
        void OnStopped(object? _, StoppedEventArgs __) => wait.Set();
        output.PlaybackStopped += OnStopped;
        try
        {
            output.Play();
            wait.Wait(TimeSpan.FromSeconds(3));
        }
        finally
        {
            output.PlaybackStopped -= OnStopped;
            try { output.Stop(); } catch { }
        }
    }

    private static WasapiOut BuildOutput(MMDevice mm)
        => new(mm, AudioClientShareMode.Shared, true, SharedWasapiLatencyMs);
}
