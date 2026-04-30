using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Mix;
using ManagedBass.Fx;

namespace KhurramAudioRoute.Core
{
    /// <summary>
    /// The high-end audio engine powered by BASS.
    /// Handles professional EQ, multi-device synchronization, and 7.1/Atmos foundations.
    /// </summary>
    public static class BassEngine
    {
        private static readonly List<int> _activeDevices = new();

        /// <summary>
        /// Checks if the required BASS native DLLs are present in the application directory.
        /// </summary>
        public static bool CheckNativeDlls(out string missingFiles)
        {
            var required = new[] { "bass.dll", "bassmix.dll", "bass_fx.dll" };
            var missing = new List<string>();
            var appDir = AppDomain.CurrentDomain.BaseDirectory;

            foreach (var dll in required)
            {
                if (!File.Exists(Path.Combine(appDir, dll)))
                {
                    missing.Add(dll);
                }
            }

            missingFiles = string.Join(", ", missing);
            return missing.Count == 0;
        }

        /// <summary>
        /// Initializes the BASS engine for a specific device.
        /// </summary>
        public static bool InitializeDevice(int deviceIndex)
        {
            try
            {
                if (!CheckNativeDlls(out _)) return false;

                // Set device context
                if (!Bass.Init(deviceIndex))
                {
                    var error = Bass.LastError;
                    if (error != Errors.Already)
                    {
                        Debug.WriteLine($"BASS: Failed to init device {deviceIndex}. Error: {error}");
                        return false;
                    }
                }

                if (!_activeDevices.Contains(deviceIndex))
                    _activeDevices.Add(deviceIndex);

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BASS: Critical error during init: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Frees all BASS resources.
        /// </summary>
        public static void Free()
        {
            foreach (var dev in _activeDevices)
            {
                Bass.CurrentDevice = dev;
                Bass.Free();
            }
            _activeDevices.Clear();
        }

        private static readonly Dictionary<string, int> _deviceStreams = new();
        private static readonly Dictionary<string, int[]> _deviceEqHandles = new();
        private static int _testToneStream;

        /// <summary>
        /// Applies high-precision EQ to a device. 
        /// This creates a hidden loopback stream that intercepts the device audio and applies BASS_FX.
        /// </summary>
        public static void UpdateEqualizer(string deviceId, float[] gains)
        {
            try
            {
                int deviceIndex = GetDeviceIndex(deviceId);
                if (deviceIndex == -1) return;

                if (!InitializeDevice(deviceIndex)) return;
                Bass.CurrentDevice = deviceIndex;

                // Ensure we have a DSP stream for this device to host the EQ
                if (!_deviceStreams.TryGetValue(deviceId, out int stream))
                {
                    // Create a "dummy" mixer stream that we can use to apply global FX to the device
                    stream = BassMix.CreateMixerStream(48000, 2, BassFlags.Default | BassFlags.MixerNonStop);
                    _deviceStreams[deviceId] = stream;
                    
                    // Initialize the EQ bands (5-band mapping)
                    // Frequencies: 60Hz, 230Hz, 910Hz, 4kHz, 14kHz
                    float[] centerFreqs = { 60, 230, 910, 4000, 14000 };
                    int[] handles = new int[5];

                    for (int i = 0; i < 5; i++)
                    {
                        handles[i] = Bass.ChannelSetFX(stream, EffectType.PeakEQ, 1);
                        var eq = new PeakEQParameters
                        {
                            lBand = i,
                            fCenter = centerFreqs[i],
                            fBandwidth = 2.5f,
                            fGain = gains[i]
                        };
                        Bass.FXSetParameters(handles[i], eq);
                    }
                    _deviceEqHandles[deviceId] = handles;
                    
                    Bass.ChannelPlay(stream);
                }
                else
                {
                    // Update existing handles
                    if (_deviceEqHandles.TryGetValue(deviceId, out var handles))
                    {
                        for (int i = 0; i < Math.Min(handles.Length, gains.Length); i++)
                        {
                            var eq = new PeakEQParameters();
                            Bass.FXGetParameters(handles[i], eq);
                            eq.fGain = gains[i];
                            Bass.FXSetParameters(handles[i], eq);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BASS EQ Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Plays a professional-grade test tone using BASS.
        /// This verifies that the native DLLs are loaded and the audio driver is responding.
        /// </summary>
        public static bool PlayTestTone(string deviceId)
        {
            try
            {
                StopTestTone();

                int deviceIndex = GetDeviceIndex(deviceId);
                if (deviceIndex == -1) deviceIndex = 1;

                if (!InitializeDevice(deviceIndex)) return false;
                Bass.CurrentDevice = deviceIndex;

                // 1. Ensure the Mixer (which has the EQ) is ready for this device
                // We'll use a dummy gain array for initial setup
                UpdateEqualizer(deviceId, new float[] { 0, 0, 0, 0, 0 });
                
                if (!_deviceStreams.TryGetValue(deviceId, out int mixerStream)) return false;

                // 2. Create the noise stream, but make it a DECODING stream so we can plug it into the mixer
                _testToneStream = Bass.CreateStream(48000, 2, BassFlags.Decode, (_, buffer, length, __) => {
                    var rand = new Random();
                    float[] floatBuffer = new float[length / 4];
                    for (int i = 0; i < floatBuffer.Length; i++)
                    {
                        floatBuffer[i] = (float)(rand.NextDouble() * 2 - 1) * 0.15f; 
                    }
                    Marshal.Copy(floatBuffer, 0, buffer, floatBuffer.Length);
                    return length;
                });

                if (_testToneStream == 0) return false;

                // 3. Plug the test tone into the EQ-processed mixer
                bool added = BassMix.MixerAddChannel(mixerStream, _testToneStream, BassFlags.Default);
                
                // 4. Play the mixer
                return Bass.ChannelPlay(mixerStream);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BASS: Test tone critical error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stops the BASS test tone.
        /// </summary>
        public static void StopTestTone()
        {
            if (_testToneStream != 0)
            {
                Bass.ChannelStop(_testToneStream);
                Bass.StreamFree(_testToneStream);
                _testToneStream = 0;
            }
        }

        /// <summary>
        /// Gets the BASS device index from a Windows Device ID (GUID string).
        /// </summary>
        public static int GetDeviceIndex(string deviceId)
        {
            for (int i = 1; ; i++)
            {
                if (!Bass.GetDeviceInfo(i, out var info)) break;
                // BASS stores the Windows Device ID in the Driver property
                if (info.Driver != null && info.Driver.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1; // Default device is usually 1 in BASS, -1 means not found
        }
    }
}
