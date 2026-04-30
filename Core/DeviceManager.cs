using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace KhurramAudioRoute.Core
{
    public partial class AudioDevice : ObservableObject
    {
        private string? _id;
        public string? Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        private string? _name;
        public string? Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        private bool _isDefault;
        public bool IsDefault
        {
            get => _isDefault;
            set => SetProperty(ref _isDefault, value);
        }

        private float _peakValue;
        public float PeakValue
        {
            get => _peakValue;
            set => SetProperty(ref _peakValue, value);
        }

        private float _volume;
        public float Volume
        {
            get => _volume;
            set => SetProperty(ref _volume, value);
        }

        private bool _isMuted;
        public bool IsMuted
        {
            get => _isMuted;
            set => SetProperty(ref _isMuted, value);
        }

        private bool _isDuplicating;
        public bool IsDuplicating
        {
            get => _isDuplicating;
            set => SetProperty(ref _isDuplicating, value);
        }

        private bool _isAdvancedExpanded;
        public bool IsAdvancedExpanded
        {
            get => _isAdvancedExpanded;
            set => SetProperty(ref _isAdvancedExpanded, value);
        }

        private string _duplicateStatus = "No duplicate targets active";
        public string DuplicateStatus
        {
            get => _duplicateStatus;
            set => SetProperty(ref _duplicateStatus, value);
        }

        private bool _isDuplicateBusy;
        public bool IsDuplicateBusy
        {
            get => _isDuplicateBusy;
            set => SetProperty(ref _isDuplicateBusy, value);
        }

        private ObservableCollection<DeviceSelection> _duplicateTargets = new();
        public ObservableCollection<DeviceSelection> DuplicateTargets
        {
            get => _duplicateTargets;
            set => SetProperty(ref _duplicateTargets, value);
        }

        private float _eqLow = 0f;
        public float EqLow
        {
            get => _eqLow;
            set => SetProperty(ref _eqLow, value);
        }

        private float _eqLowMid = 0f;
        public float EqLowMid
        {
            get => _eqLowMid;
            set => SetProperty(ref _eqLowMid, value);
        }

        private float _eqMid = 0f;
        public float EqMid
        {
            get => _eqMid;
            set => SetProperty(ref _eqMid, value);
        }

        private float _eqHighMid = 0f;
        public float EqHighMid
        {
            get => _eqHighMid;
            set => SetProperty(ref _eqHighMid, value);
        }

        private float _eqHigh = 0f;
        public float EqHigh
        {
            get => _eqHigh;
            set => SetProperty(ref _eqHigh, value);
        }

        public float[] GetEqualizerGains()
            => new[] { EqLow, EqLowMid, EqMid, EqHighMid, EqHigh };

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

        public static void SetMasterMute(string deviceId, bool mute)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);
                device.AudioEndpointVolume.Mute = mute;
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

        public static List<AudioDevice> GetCaptureDevices()
        {
            var devices = new List<AudioDevice>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

                MMDevice? defaultDevice = null;
                try { defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); } catch { }

                foreach (var endpoint in endpoints)
                {
                    float peak = 0;
                    float vol = 1.0f;
                    bool muted = false;
                    try { peak = endpoint.AudioMeterInformation.MasterPeakValue; } catch { }
                    try
                    {
                        vol = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
                        muted = endpoint.AudioEndpointVolume.Mute;
                    }
                    catch { }

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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in GetCaptureDevices: {ex.Message}");
            }

            return devices;
        }

        public static void UpdateDeviceLevels(IEnumerable<AudioDevice> devices)
        {
            var deviceMap = devices
                .Where(d => !string.IsNullOrWhiteSpace(d.Id))
                .ToDictionary(d => d.Id!);

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice? defaultDevice = null;

                try { defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console); } catch { }

                foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    try
                    {
                        if (!deviceMap.TryGetValue(endpoint.ID, out var device))
                            continue;

                        try { device.PeakValue = endpoint.AudioMeterInformation.MasterPeakValue; } catch { }
                        try
                        {
                            device.Volume = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
                            device.IsMuted = endpoint.AudioEndpointVolume.Mute;
                        }
                        catch { }

                        device.IsDefault = defaultDevice != null && endpoint.ID == defaultDevice.ID;
                    }
                    catch { }
                    finally { endpoint.Dispose(); }
                }

                defaultDevice?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateDeviceLevels error: {ex.Message}");
            }
        }
    }
}
