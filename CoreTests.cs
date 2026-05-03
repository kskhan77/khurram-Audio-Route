using System;
using System.Diagnostics;
using System.Linq;
using KhurramAudioRoute.Core;
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
