using System;

namespace KhurramAudioRoute.Core.SyncCalibration.L3;

/// <summary>
/// Logarithmic sine sweep ("chirp") generator for L3 mic-based auto-sync.
/// See <c>docs/LATENCY_PLAN.md</c> phase L3.
/// </summary>
/// <remarks>
/// The sweep is the reference signal that mic capture is cross-correlated against.
/// We use a log-sweep (rather than linear) because it puts roughly equal energy
/// per octave, which matches human hearing and most loudspeakers' frequency
/// response. Hann fade-in/fade-out windows on the first and last 10 ms suppress
/// click artefacts at sweep boundaries.
/// </remarks>
public static class LogSweepGenerator
{
    public const int DefaultSampleRate = 48000;
    public const float DefaultDurationSeconds = 0.25f;
    public const float DefaultStartHz = 200f;
    public const float DefaultEndHz = 12000f;

    /// <summary>
    /// Generate a mono log-sweep at the given sample rate.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz (e.g. 48000).</param>
    /// <param name="durationSeconds">Sweep length, typically 0.25 s.</param>
    /// <param name="startHz">Lower frequency bound (e.g. 200 Hz).</param>
    /// <param name="endHz">Upper frequency bound (e.g. 12000 Hz).</param>
    /// <param name="peakAmplitude">Peak amplitude in [0..1]. 0.5 leaves headroom.</param>
    public static float[] Generate(
        int sampleRate = DefaultSampleRate,
        float durationSeconds = DefaultDurationSeconds,
        float startHz = DefaultStartHz,
        float endHz = DefaultEndHz,
        float peakAmplitude = 0.5f)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (durationSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if (startHz <= 0 || endHz <= 0 || endHz <= startHz)
            throw new ArgumentOutOfRangeException(nameof(endHz), "Need endHz > startHz > 0.");

        int n = (int)Math.Round(sampleRate * (double)durationSeconds);
        if (n < 16) throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Sweep too short.");

        float[] buf = new float[n];
        double t1 = (double)durationSeconds;
        double k = Math.Log(endHz / (double)startHz);

        // Phase of a log sweep at time t:
        //   phi(t) = 2π · f0 · T / k · (exp(k·t/T) − 1)
        double phaseScale = 2.0 * Math.PI * startHz * t1 / k;

        int fade = Math.Min(n / 16, sampleRate / 100);
        if (fade < 4) fade = 4;

        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sampleRate;
            double phase = phaseScale * (Math.Exp(k * t / t1) - 1.0);
            double s = Math.Sin(phase);

            double w = 1.0;
            if (i < fade)
            {
                double x = (i + 1) / (double)fade;
                w = 0.5 * (1.0 - Math.Cos(Math.PI * x)); // Hann ramp up
            }
            else if (i >= n - fade)
            {
                double x = (n - i) / (double)fade;
                w = 0.5 * (1.0 - Math.Cos(Math.PI * x)); // Hann ramp down
            }

            buf[i] = (float)(peakAmplitude * w * s);
        }

        return buf;
    }
}
