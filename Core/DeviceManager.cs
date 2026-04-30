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

        // 10-band ISO-octave graphic EQ. Centers and Q values must stay aligned with
        // BassEngine.UpdateEqualizer and EqualizerSampleProvider so the loopback layer
        // and the mirror path produce the same shape.
        private float _eqBand0;
        public float EqBand0 { get => _eqBand0; set => SetProperty(ref _eqBand0, value); }

        private float _eqBand1;
        public float EqBand1 { get => _eqBand1; set => SetProperty(ref _eqBand1, value); }

        private float _eqBand2;
        public float EqBand2 { get => _eqBand2; set => SetProperty(ref _eqBand2, value); }

        private float _eqBand3;
        public float EqBand3 { get => _eqBand3; set => SetProperty(ref _eqBand3, value); }

        private float _eqBand4;
        public float EqBand4 { get => _eqBand4; set => SetProperty(ref _eqBand4, value); }

        private float _eqBand5;
        public float EqBand5 { get => _eqBand5; set => SetProperty(ref _eqBand5, value); }

        private float _eqBand6;
        public float EqBand6 { get => _eqBand6; set => SetProperty(ref _eqBand6, value); }

        private float _eqBand7;
        public float EqBand7 { get => _eqBand7; set => SetProperty(ref _eqBand7, value); }

        private float _eqBand8;
        public float EqBand8 { get => _eqBand8; set => SetProperty(ref _eqBand8, value); }

        private float _eqBand9;
        public float EqBand9 { get => _eqBand9; set => SetProperty(ref _eqBand9, value); }

        public float[] GetEqualizerGains()
            => new[] { EqBand0, EqBand1, EqBand2, EqBand3, EqBand4, EqBand5, EqBand6, EqBand7, EqBand8, EqBand9 };

        public void SetEqualizerGains(float[] gains)
        {
            if (gains == null) return;
            EqBand0 = gains.Length > 0 ? gains[0] : 0f;
            EqBand1 = gains.Length > 1 ? gains[1] : 0f;
            EqBand2 = gains.Length > 2 ? gains[2] : 0f;
            EqBand3 = gains.Length > 3 ? gains[3] : 0f;
            EqBand4 = gains.Length > 4 ? gains[4] : 0f;
            EqBand5 = gains.Length > 5 ? gains[5] : 0f;
            EqBand6 = gains.Length > 6 ? gains[6] : 0f;
            EqBand7 = gains.Length > 7 ? gains[7] : 0f;
            EqBand8 = gains.Length > 8 ? gains[8] : 0f;
            EqBand9 = gains.Length > 9 ? gains[9] : 0f;
        }

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
