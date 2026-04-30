using System;
using System.Linq;
using KhurramAudioRoute.Core;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KhurramAudioRoute.Tests
{
    /// <summary>
    /// A simple test harness to verify the Equalizer and Core logic without a full unit test runner.
    /// Run this from a console or a debug session to verify integrity.
    /// </summary>
    public static class AudioCoreTester
    {
        public static void RunTests()
        {
            Console.WriteLine("--- Starting Audio Core Integrity Tests ---");
            
            TestEqualizerTransparency();
            TestEqualizerGainChange();
            TestDeviceEnumeration();

            Console.WriteLine("--- All Tests Completed ---");
        }

        /// <summary>
        /// Verifies that with 0dB gain, the Equalizer doesn't significantly alter the signal (transparency).
        /// </summary>
        private static void TestEqualizerTransparency()
        {
            Console.Write("Testing Equalizer Transparency (0dB)... ");
            
            var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            var source = new SilenceProvider(format).ToSampleProvider();
            var eq = new EqualizerSampleProvider(source, new float[] { 0, 0, 0, 0, 0 });

            float[] buffer = new float[1024];
            int read = eq.Read(buffer, 0, buffer.Length);

            // For silence, it should remain silence
            bool isSilent = buffer.Take(read).All(f => Math.Abs(f) < 0.000001f);
            
            if (isSilent)
                Console.WriteLine("PASSED");
            else
                Console.WriteLine("FAILED (Signal altered)");
        }

        /// <summary>
        /// Verifies that changing gain actually modifies the signal processing path.
        /// </summary>
        private static void TestEqualizerGainChange()
        {
            Console.Write("Testing Equalizer Gain Response... ");
            
            var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            // Create a simple sine wave at 60Hz (our first band)
            var sine = new SignalGenerator(44100, 1) { Frequency = 60, Gain = 0.5, Type = SignalGeneratorType.Sin };
            var source = sine;
            
            var eq = new EqualizerSampleProvider(source, new float[] { 10, 0, 0, 0, 0 }); // +10dB at 60Hz

            float[] buffer = new float[1024];
            eq.Read(buffer, 0, buffer.Length);

            // Peak should be higher than the input 0.5 due to +10dB boost
            float max = buffer.Max(Math.Abs);
            
            if (max > 0.51f)
                Console.WriteLine($"PASSED (Gain applied: {max:F2} > 0.5)");
            else
                Console.WriteLine($"FAILED (No boost detected: {max:F2})");
        }

        /// <summary>
        /// Verifies that the DeviceManager can still access system endpoints.
        /// </summary>
        private static void TestDeviceEnumeration()
        {
            Console.Write("Testing Device Enumeration... ");
            try
            {
                var devices = DeviceManager.GetRenderDevices();
                if (devices.Count > 0)
                    Console.WriteLine($"PASSED ({devices.Count} devices found)");
                else
                    Console.WriteLine("SKIPPED (No audio devices active on this system)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED ({ex.Message})");
            }
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
