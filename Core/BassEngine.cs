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
        private static bool _isInitialized;
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

                _isInitialized = true;
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
            _isInitialized = false;
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
