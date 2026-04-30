using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace KhurramAudioRoute.Core
{
    public class AudioDevice
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public bool IsDefault { get; set; }
        public float PeakValue { get; set; }
        public float Volume { get; set; }
        public bool IsMuted { get; set; }

        public override string ToString() => Name ?? "Unknown Device";
    }

    public static class DeviceManager
    {
        public static void SetMasterVolume(string deviceId, float volume)
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    var device = enumerator.GetDevice(deviceId);
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
                    device.AudioEndpointVolume.Mute = false; // Auto-unmute when volume changed
                }
            }
            catch { }
        }

        public static List<AudioDevice> GetRenderDevices()
        {
            var devices = new List<AudioDevice>();
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    
                    MMDevice? defaultDevice = null;
                    try { defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console); } catch { }

                    foreach (var endpoint in endpoints)
                    {
                        float peak = 0;
                        float vol = 1.0f;
                        bool muted = false;
                        try { peak = endpoint.AudioMeterInformation.MasterPeakValue; } catch { }
                        try { 
                            vol = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
                            muted = endpoint.AudioEndpointVolume.Mute;
                        } catch { }

                        devices.Add(new AudioDevice
                        {
                            Id = endpoint.ID,
                            Name = endpoint.FriendlyName,
                            IsDefault = defaultDevice != null && endpoint.ID == defaultDevice.ID,
                            PeakValue = peak,
                            Volume = vol,
                            IsMuted = muted
                        });
                        endpoint.Dispose();
                    }
                    defaultDevice?.Dispose();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in GetRenderDevices: {ex.Message}");
            }
            return devices;
        }
    }
}
