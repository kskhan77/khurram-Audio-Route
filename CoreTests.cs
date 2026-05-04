using System;
using System.Diagnostics;
using System.Linq;
using KhurramAudioRoute.Core;
using KhurramAudioRoute.Core.SyncCalibration.L3;
using KhurramAudioRoute.Core.Voice;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KhurramAudioRoute.Tests
{
    /// <summary>
    /// A simple test harness to verify equalizer enumeration without a separate test runner.
    /// Routed from UI (Other Options diagnostics). Mirrors lines to Debug so Output window captures them during regression.
    /// </summary>
    public static class AudioCoreTester
    {
        public static void RunTests()
        {
            LogLine("--- Starting Audio Core Integrity Tests ---");

            TestEqualizerTransparency();
            TestEqualizerGainChange();
            TestDeviceEnumeration();
            TestL3CrossCorrelation();
            TestNoiseGate();

            LogLine("--- All Tests Completed ---");
        }

        private static void LogLine(string msg)
        {
            Debug.WriteLine(msg);
            Console.WriteLine(msg);
            Trace.WriteLine(msg);
        }

        private static void LogLine(string prefix, bool passed, string? detail = null)
        {
            var tail = detail == null ? string.Empty : $" {detail}";
            LogLine(passed ? $"{prefix}: PASSED{tail}" : $"{prefix}: FAILED{tail}");
        }

        /// <summary>
        /// Verifies that with 0dB gain, the Equalizer doesn't significantly alter silence (transparency baseline).
        /// </summary>
        private static void TestEqualizerTransparency()
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            var source = new SilenceProvider(format).ToSampleProvider();
            var eq = new EqualizerSampleProvider(source, new float[10]);

            float[] buffer = new float[1024];
            int read = eq.Read(buffer, 0, buffer.Length);

            bool isSilent = buffer.Take(read).All(f => Math.Abs(f) < 0.000001f);
            LogLine("Equalizer transparency (0 dB gains on silence)",
                isSilent,
                isSilent ? null : "(signal unexpectedly non-zero)");
        }

        /// <summary>
        /// Verifies that changing gain actually modifies the signal processing path.
        /// </summary>
        private static void TestEqualizerGainChange()
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            var gains = new float[10];
            gains[0] = 10f;
            var eq = new EqualizerSampleProvider(new SineSampleProvider(format, 31, 0.5f), gains);

            float[] buffer = new float[1024];
            eq.Read(buffer, 0, buffer.Length);

            float max = buffer.Max(Math.Abs);
            bool passed = max > 0.51f;
            LogLine("Equalizer gain response (+10 dB at band 0)", passed,
                passed ? $"peak {max:F2}" : $"peak {max:F2}, expected boost over 0.5");
        }

        /// <summary>
        /// L3 math smoke check: synthesize a captured signal as the log-sweep
        /// delayed by a known number of samples plus white noise, then verify
        /// the FFT cross-correlator recovers the lag within +/-1 sample with
        /// SNR comfortably above the gate.
        /// </summary>
        private static void TestL3CrossCorrelation()
        {
            const int sampleRate = 48000;
            const int knownLag = 137; // ~2.85 ms — representative speaker→mic distance
            const float noisePeak = 0.05f;
            const float snrGateDb = 12f;

            float[] sweep = LogSweepGenerator.Generate(sampleRate);

            // capture window: 1.5 s, sweep starts at knownLag, rest is silence + noise
            int captureLen = sampleRate * 3 / 2;
            float[] captured = new float[captureLen];
            var rng = new Random(1859);
            for (int i = 0; i < captureLen; i++)
                captured[i] = (float)((rng.NextDouble() * 2.0 - 1.0) * noisePeak);
            int copyLen = Math.Min(sweep.Length, captureLen - knownLag);
            for (int i = 0; i < copyLen; i++)
                captured[knownLag + i] += sweep[i];

            int maxLag = sampleRate / 2; // 500 ms ceiling, per LATENCY_PLAN
            var result = CrossCorrelator.Correlate(captured, sweep, maxLag);

            int err = Math.Abs(result.LagSamples - knownLag);
            bool lagOk = err <= 1;
            bool snrOk = result.SnrDb >= snrGateDb;
            bool passed = lagOk && snrOk;

            string detail = $"lag {result.LagSamples} (expected {knownLag}, err {err}), SNR {result.SnrDb:F1} dB";
            LogLine("L3 cross-correlation (synthetic sweep at known lag)", passed,
                passed ? detail : detail + " — fail (lag>1 sample or SNR<gate)");
        }

        /// <summary>
        /// Mic chain Phase 1: synthesize quiet→tone→quiet, run through the gate,
        /// and verify the quiet segments are suppressed below -40 dBFS RMS while
        /// the tone segment passes within ~6 dB of the source level.
        /// </summary>
        private static void TestNoiseGate()
        {
            const int sampleRate = 48000;
            const int segmentLen = sampleRate / 5; // 200 ms each
            int total = segmentLen * 3;

            var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
            float[] src = new float[total];
            var rng = new Random(7);

            float quiet = NoiseGate.LinearFromDb(-60f);
            float toneAmp = NoiseGate.LinearFromDb(-12f);

            for (int i = 0; i < segmentLen; i++)
                src[i] = (float)((rng.NextDouble() * 2.0 - 1.0) * quiet);
            for (int i = 0; i < segmentLen; i++)
                src[segmentLen + i] = (float)(toneAmp * Math.Sin(2.0 * Math.PI * 440.0 * i / sampleRate));
            for (int i = 0; i < segmentLen; i++)
                src[segmentLen * 2 + i] = (float)((rng.NextDouble() * 2.0 - 1.0) * quiet);

            var srcProvider = new ArrayFloatProvider(src, format);
            var gate = new NoiseGate(srcProvider);

            float[] outBuf = new float[total];
            int read = gate.Read(outBuf, 0, total);

            // Skip the trailing close ramp so we measure steady-state silence.
            int closeRampSamples = (int)(gate.CloseMs * sampleRate / 1000f);
            int holdSamples = (int)(gate.HoldMs * sampleRate / 1000f);
            int quietStart = segmentLen * 2 + holdSamples + closeRampSamples + sampleRate / 100;
            int quietEnd = read;

            float toneRms = Rms(outBuf, segmentLen + sampleRate / 50, segmentLen - sampleRate / 50);
            float quietRms = quietEnd > quietStart ? Rms(outBuf, quietStart, quietEnd - quietStart) : 0f;

            float toneDb = NoiseGate.DbFromLinear(toneRms);
            float quietDb = NoiseGate.DbFromLinear(quietRms);

            bool quietSuppressed = quietDb < -50f;
            bool tonePassed = toneDb > -20f;
            bool passed = quietSuppressed && tonePassed;
            string detail = $"tone {toneDb:F1} dBFS, quiet {quietDb:F1} dBFS";
            LogLine("Noise gate (quiet → tone → quiet)", passed,
                passed ? detail : detail + " — quiet should be < -50 dBFS, tone > -20 dBFS");
        }

        private static float Rms(float[] buf, int offset, int count)
        {
            if (count <= 0) return 0f;
            int end = Math.Min(offset + count, buf.Length);
            double sumSq = 0;
            int n = 0;
            for (int i = offset; i < end; i++)
            {
                double v = buf[i];
                sumSq += v * v;
                n++;
            }
            return n > 0 ? (float)Math.Sqrt(sumSq / n) : 0f;
        }

        /// <summary>
        /// Verifies that the DeviceManager can still access system endpoints.
        /// </summary>
        private static void TestDeviceEnumeration()
        {
            try
            {
                var devices = DeviceManager.GetRenderDevices();
                if (devices.Count > 0)
                    LogLine("Device enumeration (render)", true, $"{devices.Count} endpoint(s)");
                else
                    LogLine("Device enumeration (render)", true, "(0 devices — SKIPPED, no endpoints)");
            }
            catch (Exception ex)
            {
                LogLine("Device enumeration (render)", false, ex.Message);
            }
        }
    }

    /// <summary>Reads from a fixed float array — used by Phase 1 mic-chain test.</summary>
    internal sealed class ArrayFloatProvider : ISampleProvider
    {
        private readonly float[] _data;
        private int _position;

        public ArrayFloatProvider(float[] data, WaveFormat format)
        {
            _data = data;
            WaveFormat = format;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int remaining = _data.Length - _position;
            int n = Math.Min(count, remaining);
            if (n <= 0) return 0;
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }

    /// <summary>Mono sine for gain-path smoke tests (no dependency on SignalGenerator wave graph).</summary>
    internal sealed class SineSampleProvider : ISampleProvider
    {
        private readonly double _inc;
        private double _phase;

        public SineSampleProvider(WaveFormat format, double frequencyHz, float peakAmplitude)
        {
            if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.Channels != 1)
                throw new ArgumentException("Expected IEEE float mono.", nameof(format));
            WaveFormat = format;
            _inc = 2 * Math.PI * frequencyHz / format.SampleRate;
            Peak = peakAmplitude;
        }

        public WaveFormat WaveFormat { get; }
        public float Peak { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                buffer[offset + i] = Peak * (float)Math.Sin(_phase);
                _phase += _inc;
                if (_phase > Math.PI * 1024) _phase -= Math.PI * 1024;
            }
            return count;
        }
    }

    /// <summary>
    /// Helper to provide silence for testing
    /// </summary>
    public class SilenceProvider : IWaveProvider
    {
        public WaveFormat WaveFormat { get; }
        public SilenceProvider(WaveFormat format) => WaveFormat = format;
        public int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
    }
}
