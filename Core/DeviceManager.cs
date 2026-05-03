using CommunityToolkit.Mvvm.ComponentModel;
using KhurramAudioRoute.Core.Latency;
using KhurramAudioRoute.Core.Spatial;
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
            set
            {
                if (SetProperty(ref _name, value))
                    OnPropertyChanged(nameof(UiListLabel));
            }
        }

        private bool _isDefault;
        public bool IsDefault
        {
            get => _isDefault;
            set
            {
                if (SetProperty(ref _isDefault, value))
                    OnPropertyChanged(nameof(UiListLabel));
            }
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

        /// <summary>Virtual profile mirror list: collapsed saves vertical space (no scrollbar).</summary>
        private bool _mirrorOutputsListExpanded = true;
        public bool MirrorOutputsListExpanded
        {
            get => _mirrorOutputsListExpanded;
            set => SetProperty(ref _mirrorOutputsListExpanded, value);
        }

        // Pre-fan-out delay added before every mirror target's per-target offset.
        // Used to align this device's mirrored copies with its OS-level playback.
        private int _sourceLatencyMs;
        public int SourceLatencyMs
        {
            get => _sourceLatencyMs;
            set => SetProperty(ref _sourceLatencyMs, value);
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

        private bool _isSonicFlowVirtual;
        public bool IsSonicFlowVirtual
        {
            get => _isSonicFlowVirtual;
            set => SetProperty(ref _isSonicFlowVirtual, value);
        }

        private SpatialPreset _spatialPreset = SpatialPreset.Off;
        public SpatialPreset SpatialPreset
        {
            get => _spatialPreset;
            set
            {
                if (!SetProperty(ref _spatialPreset, value))
                    return;
                OnPropertyChanged(nameof(IsSpatialPresetActive));
            }
        }

        /// <summary>Whether spatial processing runs (toggle binds here; presets still choose the scene).</summary>
        public bool IsSpatialPresetActive
        {
            get => SpatialPreset != SpatialPreset.Off;
            set
            {
                if (value)
                {
                    if (SpatialPreset == SpatialPreset.Off)
                        SpatialPreset = SpatialPreset.HeadphoneStereoPlus;
                    return;
                }
                if (SpatialPreset != SpatialPreset.Off)
                    SpatialPreset = SpatialPreset.Off;
            }
        }

        // Static enum source for the per-card ComboBox binding (XAML can't easily
        // call Enum.GetValues, and ObjectDataProvider would mean another resource).
        public static SpatialPreset[] AllSpatialPresets { get; } = Enum.GetValues<SpatialPreset>();

        private bool _canHostMirroring = true;
        public bool CanHostMirroring
        {
            get => _canHostMirroring;
            set => SetProperty(ref _canHostMirroring, value);
        }

        /// <summary>
        /// Whether this real output participates in the master bus fan-out.
        /// Defaults to <c>true</c> so devices light up automatically when the
        /// user flips master power on. Toggling this to <c>false</c> removes
        /// the device from the bridge without disturbing other outputs. Only
        /// meaningful for physical (non-virtual) playback endpoints; the bus
        /// device itself ignores it.
        /// </summary>
        private bool _isActiveOutput = true;
        public bool IsActiveOutput
        {
            get => _isActiveOutput;
            set => SetProperty(ref _isActiveOutput, value);
        }

        /// <summary>
        /// Coarse class assigned by <see cref="DeviceClassResolver"/> at
        /// enumeration time. Drives the small chip on each device card and
        /// the L1 default <see cref="TargetLatencyOffsetMs"/>.
        /// </summary>
        private DeviceClass _deviceClass = DeviceClass.Unknown;
        public DeviceClass DeviceClass
        {
            get => _deviceClass;
            set
            {
                if (SetProperty(ref _deviceClass, value))
                    OnPropertyChanged(nameof(DeviceClassLabel));
            }
        }

        public string DeviceClassLabel => DeviceClassInfo.ShortLabel(DeviceClass);

        /// <summary>
        /// Per-physical-device sync offset used by the master engine bridge.
        /// Independent of <see cref="DuplicateTargets"/>'s legacy mirror
        /// offsets — the master bus fans out via
        /// <c>BassEngine.UpdateBridgeTargetLatency</c> using this value.
        /// Defaults to <see cref="DeviceClassInfo.DefaultOffsetMs"/> for the
        /// detected class and is persisted in <c>UserSettings</c>.
        /// </summary>
        private int _targetLatencyOffsetMs;
        public int TargetLatencyOffsetMs
        {
            get => _targetLatencyOffsetMs;
            set => SetProperty(ref _targetLatencyOffsetMs, value);
        }

        /// <summary>
        /// L2 wizard fingerprint no longer matches live WASAPI mix (refreshed each <c>RefreshData</c>).
        /// </summary>
        private bool _syncCalibrationStale;
        public bool SyncCalibrationStale
        {
            get => _syncCalibrationStale;
            set => SetProperty(ref _syncCalibrationStale, value);
        }

        /// <summary>
        /// Caption like "Auto-synced 3 days ago" when an L3 row exists for this
        /// device, otherwise empty. Refreshed by <c>MainViewModel.RefreshData</c>
        /// and after the AutoSyncWindow closes.
        /// </summary>
        private string _autoSyncCaption = string.Empty;
        public string AutoSyncCaption
        {
            get => _autoSyncCaption;
            set
            {
                if (SetProperty(ref _autoSyncCaption, value ?? string.Empty))
                    OnPropertyChanged(nameof(HasAutoSyncCaption));
            }
        }

        public bool HasAutoSyncCaption => !string.IsNullOrEmpty(_autoSyncCaption);

        /// <summary>
        /// Saved L3 fingerprint disagrees with the current WASAPI mix format
        /// (refreshed each <c>RefreshData</c> + after Auto-sync apply). Drives
        /// the DRIFT? chip on the device card.
        /// </summary>
        private bool _autoSyncDrift;
        public bool AutoSyncDrift
        {
            get => _autoSyncDrift;
            set => SetProperty(ref _autoSyncDrift, value);
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

        private string? _profileLabel;
        public string? ProfileLabel
        {
            get => _profileLabel;
            set
            {
                if (SetProperty(ref _profileLabel, value))
                    OnPropertyChanged(nameof(UiListLabel));
            }
        }

        /// <summary>
        /// Single-line label for combo boxes and mirror lists (profile + device name when applicable).
        /// </summary>
        public string UiListLabel
        {
            get
            {
                var n = string.IsNullOrWhiteSpace(Name) ? "Unknown device" : Name.Trim();
                var def = IsDefault ? " · Default" : "";
                return !string.IsNullOrWhiteSpace(ProfileLabel)
                    ? $"{ProfileLabel} — {n}{def}"
                    : n + def;
            }
        }

        private bool _isEqGraphVisible;
        public bool IsEqGraphVisible
        {
            get => _isEqGraphVisible;
            set => SetProperty(ref _isEqGraphVisible, value);
        }

        /// <summary>Name of last applied EQ preset button (Flat, Bass, …) for segmented control UI.</summary>
        private string _selectedEqPresetKey = "Flat";
        public string SelectedEqPresetKey
        {
            get => _selectedEqPresetKey;
            set => SetProperty(ref _selectedEqPresetKey, value);
        }

        public override string ToString() => Name ?? "Unknown Device";

        /// <summary>Unique RadioButton group scope for segmented EQ presets on this profile card.</summary>
        public string EqPresetSegmentGroupToken => $"vf-eq-{Id ?? ProfileLabel ?? "anon"}";

        /// <summary>Radio scope for spatial mode pills per virtual profile.</summary>
        public string SpatialPresetSegmentGroupToken => $"vf-sp-{Id ?? ProfileLabel ?? "anon"}";
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

                        bool isVirtual = SonicFlowVirtualAudio.IsVirtualRenderEndpoint(endpoint.ID, endpoint.FriendlyName);
                        var deviceClass = isVirtual ? DeviceClass.Unknown : DeviceClassResolver.Detect(endpoint);

                        // L1/L4 latency: persisted id wins; else L4 name presets,
                        // else LatencyClassDefaults, else class baseline.
                        int seedOffset = LatencySeedResolver.Resolve(endpoint, deviceClass, isVirtual);

                        devices.Add(new AudioDevice
                        {
                            Id = endpoint.ID,
                            Name = endpoint.FriendlyName,
                            IsDefault = defaultDevice != null && endpoint.ID == defaultDevice.ID,
                            PeakValue = peak,
                            Volume = vol,
                            IsMuted = muted,
                            IsSonicFlowVirtual = isVirtual,
                            DeviceClass = deviceClass,
                            TargetLatencyOffsetMs = seedOffset
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
                        device.IsSonicFlowVirtual = SonicFlowVirtualAudio.IsVirtualRenderEndpoint(endpoint.ID, endpoint.FriendlyName);
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
