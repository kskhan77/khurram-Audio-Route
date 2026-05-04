using System;

namespace KhurramAudioRoute.Core.SyncCalibration.L3;

/// <summary>
/// FFT-based cross-correlation for L3 mic-based auto-sync.
/// See <c>docs/LATENCY_PLAN.md</c> phase L3.
/// </summary>
/// <remarks>
/// Given a captured signal (from the system mic) and a known reference (the
/// log-sweep we just played), the lag at which the cross-correlation peaks
/// is the time-of-flight from speaker → microphone, in samples.
///
/// Computes: r[k] = IFFT( FFT(captured) · conj(FFT(reference)) )
/// then locates the maximum and reports it as a positive lag (captured trails
/// reference) or zero if the alignment is exact.
/// </remarks>
public static class CrossCorrelator
{
    public readonly record struct Result(int LagSamples, float SnrDb, float PeakMagnitude)
    {
        public static readonly Result NoMatch = new(-1, float.NegativeInfinity, 0f);
    }

    /// <summary>
    /// Locate the lag (in samples) at which <paramref name="captured"/> best
    /// matches <paramref name="reference"/> within <paramref name="maxLagSamples"/>.
    /// </summary>
    /// <param name="captured">Mono mic capture starting at or before reference playback.</param>
    /// <param name="reference">The log-sweep that was played.</param>
    /// <param name="maxLagSamples">
    /// Maximum lag to consider (e.g. sampleRate / 2 = up to 500 ms at 48 kHz).
    /// Lags outside [0, maxLagSamples] are ignored when picking the peak.
    /// </param>
    /// <param name="snrGuardSamples">
    /// Number of samples either side of the peak excluded from the SNR
    /// noise-floor calculation. Defaults to roughly 1 ms at 48 kHz.
    /// </param>
    public static Result Correlate(
        ReadOnlySpan<float> captured,
        ReadOnlySpan<float> reference,
        int maxLagSamples,
        int snrGuardSamples = 48)
    {
        if (captured.Length == 0 || reference.Length == 0)
            return Result.NoMatch;
        if (maxLagSamples <= 0) maxLagSamples = captured.Length - 1;

        int linearLen = captured.Length + reference.Length - 1;
        int n = NextPow2(linearLen);

        var capRe = new double[n];
        var capIm = new double[n];
        var refRe = new double[n];
        var refIm = new double[n];

        for (int i = 0; i < captured.Length; i++) capRe[i] = captured[i];
        for (int i = 0; i < reference.Length; i++) refRe[i] = reference[i];

        Fft(capRe, capIm, forward: true);
        Fft(refRe, refIm, forward: true);

        // Multiply captured · conj(reference)
        for (int i = 0; i < n; i++)
        {
            double a = capRe[i], b = capIm[i];
            double c = refRe[i], d = -refIm[i]; // conjugate
            capRe[i] = a * c - b * d;
            capIm[i] = a * d + b * c;
        }

        Fft(capRe, capIm, forward: false);

        // Linear xcorr length is captured.Length + reference.Length - 1.
        // After FFT, index 0 corresponds to lag = -(reference.Length - 1).
        // We want positive lags (captured trails reference) starting from
        // lag 0, which lives at index (reference.Length - 1).
        int zeroLagIndex = reference.Length - 1;
        int maxIndex = Math.Min(zeroLagIndex + maxLagSamples, linearLen - 1);

        int peakIdx = -1;
        double peakAbs = -1.0;
        for (int i = zeroLagIndex; i <= maxIndex; i++)
        {
            double v = Math.Abs(capRe[i]);
            if (v > peakAbs)
            {
                peakAbs = v;
                peakIdx = i;
            }
        }

        if (peakIdx < 0)
            return Result.NoMatch;

        double sumSq = 0;
        int count = 0;
        for (int i = zeroLagIndex; i <= maxIndex; i++)
        {
            if (Math.Abs(i - peakIdx) <= snrGuardSamples) continue;
            double v = capRe[i];
            sumSq += v * v;
            count++;
        }
        double rms = count > 0 ? Math.Sqrt(sumSq / count) : 0.0;
        float snrDb = rms > 0 && peakAbs > 0
            ? (float)(20.0 * Math.Log10(peakAbs / rms))
            : float.PositiveInfinity;

        return new Result(peakIdx - zeroLagIndex, snrDb, (float)peakAbs);
    }

    private static int NextPow2(int v)
    {
        int n = 1;
        while (n < v) n <<= 1;
        return n;
    }

    /// <summary>In-place radix-2 Cooley–Tukey FFT. Length must be a power of two.</summary>
    private static void Fft(double[] re, double[] im, bool forward)
    {
        int n = re.Length;
        if (n == 1) return;
        if ((n & (n - 1)) != 0)
            throw new ArgumentException("Length must be a power of two.", nameof(re));

        // Bit-reversal permutation.
        int j = 0;
        for (int i = 1; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;

            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        double sign = forward ? -1.0 : 1.0;
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = sign * 2.0 * Math.PI / len;
            double wRe = Math.Cos(ang);
            double wIm = Math.Sin(ang);
            int half = len >> 1;
            for (int i = 0; i < n; i += len)
            {
                double curRe = 1.0;
                double curIm = 0.0;
                for (int k = 0; k < half; k++)
                {
                    int a = i + k;
                    int b = a + half;
                    double tRe = curRe * re[b] - curIm * im[b];
                    double tIm = curRe * im[b] + curIm * re[b];
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    double nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }

        if (!forward)
        {
            double inv = 1.0 / n;
            for (int i = 0; i < n; i++)
            {
                re[i] *= inv;
                im[i] *= inv;
            }
        }
    }
}
