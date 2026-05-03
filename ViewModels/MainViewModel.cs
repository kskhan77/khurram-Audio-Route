using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KhurramAudioRoute.Core;
using KhurramAudioRoute.Core.Spatial;
using KhurramAudioRoute.Core.SyncCalibration;
using KhurramAudioRoute.Core.SyncCalibration.L3;
using KhurramAudioRoute;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KhurramAudioRoute.ViewModels
{
    public enum DashboardSection
    {
        Outputs,
        Applications,
        Microphones,
        Tools
    }

    public partial class MainViewModel : ObservableObject
    {
        public MainViewModel()
        {
            // Pull persisted master EQ + spatial before any UI binding kicks
            // in. This is read-only access — the OnXxxChanged partial hooks
            // re-save when the user moves a slider.
            try
            {
                var savedGains = UserSettings.GetMasterEqualizerGains();
                if (savedGains.Length == 10)
                {
                    SetMasterEqualizerGains(savedGains);
                }

                MasterSpatialPreset = UserSettings.GetMasterSpatialPreset();
                MasterStereoWidth = UserSettings.GetMasterStereoWidth();
                BassEngine.SetMasterStereoWidth(MasterStereoWidth);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainViewModel master state load failed: {ex.Message}");
            }

            LoadLatencyClassDefaultsForUi();

            Power.PropertyChanged += OnPowerPropertyChanged;
        }

        private bool _latencyClassDefaultsLoading;

        /// <summary>
        /// Baseline Sync slider seed per device class for <b>new</b> Windows endpoint ids
        /// (after L4 name presets). Persisted in <see cref="UserSettings.LatencyClassDefaults"/>.
        /// </summary>
        [ObservableProperty]
        private int latencyDefaultOnBoardMs;

        [ObservableProperty]
        private int latencyDefaultUsbMs;

        [ObservableProperty]
        private int latencyDefaultHdmiMs;

        [ObservableProperty]
        private int latencyDefaultBtMs;

        [ObservableProperty]
        private int latencyDefaultNetworkMs;

        private void LoadLatencyClassDefaultsForUi()
        {
            _latencyClassDefaultsLoading = true;
            try
            {
                var m = UserSettings.GetLatencyClassDefaults();
                LatencyDefaultOnBoardMs = ReadClassDefault(m, "On-board", DeviceClassInfo.DefaultOnBoardOffsetMs);
                LatencyDefaultUsbMs = ReadClassDefault(m, "USB", DeviceClassInfo.DefaultUsbOffsetMs);
                LatencyDefaultHdmiMs = ReadClassDefault(m, "HDMI", DeviceClassInfo.DefaultHdmiOffsetMs);
                LatencyDefaultBtMs = ReadClassDefault(m, "BT", DeviceClassInfo.DefaultBluetoothOffsetMs);
                LatencyDefaultNetworkMs = ReadClassDefault(m, "Network", DeviceClassInfo.DefaultNetworkOffsetMs);
            }
            finally
            {
                _latencyClassDefaultsLoading = false;
            }
        }

        private static int ReadClassDefault(IReadOnlyDictionary<string, int> map, string key, int builtin)
            => map.TryGetValue(key, out var v) ? v : builtin;

        private void PersistLatencyClassDefault(string classKey, int value)
        {
            if (_latencyClassDefaultsLoading) return;
            UserSettings.SetLatencyClassDefault(classKey, Math.Clamp(value, 0, 120));
        }

        partial void OnLatencyDefaultOnBoardMsChanged(int value) => PersistLatencyClassDefault("On-board", value);

        partial void OnLatencyDefaultUsbMsChanged(int value) => PersistLatencyClassDefault("USB", value);

        partial void OnLatencyDefaultHdmiMsChanged(int value) => PersistLatencyClassDefault("HDMI", value);

        partial void OnLatencyDefaultBtMsChanged(int value) => PersistLatencyClassDefault("BT", value);

        partial void OnLatencyDefaultNetworkMsChanged(int value) => PersistLatencyClassDefault("Network", value);

        [RelayCommand]
        private void ResetLatencyClassDefaultsToBuiltIn()
        {
            UserSettings.ClearLatencyClassDefaults();
            LoadLatencyClassDefaultsForUi();
        }

        [ObservableProperty]
        private ObservableCollection<AppAudioSession> sessions = new();

        [ObservableProperty]
        private ObservableCollection<AudioDevice> devices = new();

        [ObservableProperty]
        private ObservableCollection<AudioDevice> virtualDevices = new();

        [ObservableProperty]
        private ObservableCollection<AudioDevice> physicalDevices = new();

        [ObservableProperty]
        private ObservableCollection<AudioDevice> microphones = new();

        [ObservableProperty]
        private AudioDevice? _masterDevice;

        [ObservableProperty]
        private AudioDevice? sonicFlowVirtualDevice;

        [ObservableProperty]
        private bool isSonicFlowVirtualDeviceInstalled;

        [ObservableProperty]
        private string sonicFlowVirtualDeviceName = SonicFlowVirtualAudio.ProductRenderName;

        [ObservableProperty]
        private string sonicFlowVirtualStatus = SonicFlowVirtualAudio.BuildStatus(null);

        [ObservableProperty]
        private DashboardSection currentSection = DashboardSection.Outputs;

        [ObservableProperty]
        private bool isTestTonePlaying;

        /// <summary>
        /// Master power switch for the SonicFlow audio bus. Owns the on/off
        /// state machine and the persisted "previous Windows default" so we
        /// can restore the user's setup when the bus disengages or the app
        /// closes. See <c>docs/AUDIO_BUS_PLAN.md</c>.
        /// </summary>
        public PowerService Power { get; } = new PowerService();

        // ── Master engine state (one EQ curve, one spatial preset for the bus) ──

        [ObservableProperty]
        private float masterEqBand0;
        [ObservableProperty]
        private float masterEqBand1;
        [ObservableProperty]
        private float masterEqBand2;
        [ObservableProperty]
        private float masterEqBand3;
        [ObservableProperty]
        private float masterEqBand4;
        [ObservableProperty]
        private float masterEqBand5;
        [ObservableProperty]
        private float masterEqBand6;
        [ObservableProperty]
        private float masterEqBand7;
        [ObservableProperty]
        private float masterEqBand8;
        [ObservableProperty]
        private float masterEqBand9;

        [ObservableProperty]
        private SpatialPreset masterSpatialPreset = SpatialPreset.Off;

        /// <summary>
        /// Mid/side stereo width applied as the first stage of every non-Off spatial preset.
        /// 1.0 = no effect; &lt;1 narrows toward mono; &gt;1 widens. Persisted in <see cref="UserSettings"/>.
        /// No audible effect when <see cref="MasterSpatialPreset"/> is Off — see docs/LATENCY_PLAN.md / SpatialPipeline.
        /// </summary>
        [ObservableProperty]
        private float masterStereoWidth = 1.0f;

        partial void OnMasterStereoWidthChanged(float value)
        {
            UserSettings.SetMasterStereoWidth(value);
            BassEngine.SetMasterStereoWidth(value);
        }

        /// <summary>Convenience for two-way binding to the Spatial card master switch.</summary>
        public bool MasterSpatialActive
        {
            get => MasterSpatialPreset != SpatialPreset.Off;
            set
            {
                if (value && MasterSpatialPreset == SpatialPreset.Off)
                    MasterSpatialPreset = SpatialPreset.HeadphoneStereoPlus;
                else if (!value && MasterSpatialPreset != SpatialPreset.Off)
                    MasterSpatialPreset = SpatialPreset.Off;
            }
        }

        partial void OnMasterSpatialPresetChanged(SpatialPreset value)
        {
            OnPropertyChanged(nameof(MasterSpatialActive));
            UserSettings.SetMasterSpatialPreset(value);
            ApplyMasterSpatialLive();
        }

        /// <summary>Token bound to the spatial preset radio group on the master card.</summary>
        public string MasterSpatialPresetGroupToken { get; } = "master-spatial-preset";

        /// <summary>
        /// Pulls the 10-band master EQ into a single array. Used both when
        /// (re)starting the bridge and when persisting changes.
        /// </summary>
        public float[] GetMasterEqualizerGains() => new[]
        {
            MasterEqBand0, MasterEqBand1, MasterEqBand2, MasterEqBand3, MasterEqBand4,
            MasterEqBand5, MasterEqBand6, MasterEqBand7, MasterEqBand8, MasterEqBand9
        };

        /// <summary>
        /// Replaces every master EQ band in one shot (e.g. when restoring from
        /// settings or applying a preset). Each setter raises its own
        /// PropertyChanged so ApplyMasterEqLive runs once per band — that is
        /// fine because <see cref="BassEngine.UpdateBridgeEqualizer"/> is cheap
        /// and idempotent.
        /// </summary>
        public void SetMasterEqualizerGains(float[] gains)
        {
            if (gains == null) return;
            MasterEqBand0 = gains.Length > 0 ? gains[0] : 0f;
            MasterEqBand1 = gains.Length > 1 ? gains[1] : 0f;
            MasterEqBand2 = gains.Length > 2 ? gains[2] : 0f;
            MasterEqBand3 = gains.Length > 3 ? gains[3] : 0f;
            MasterEqBand4 = gains.Length > 4 ? gains[4] : 0f;
            MasterEqBand5 = gains.Length > 5 ? gains[5] : 0f;
            MasterEqBand6 = gains.Length > 6 ? gains[6] : 0f;
            MasterEqBand7 = gains.Length > 7 ? gains[7] : 0f;
            MasterEqBand8 = gains.Length > 8 ? gains[8] : 0f;
            MasterEqBand9 = gains.Length > 9 ? gains[9] : 0f;
        }

        /// <summary>Resets the master EQ to flat (0 dB across all bands).</summary>
        [RelayCommand]
        public void ResetMasterEqualizer() => ApplyMasterEqPreset(EqualizerPresets.Flat);

        /// <summary>
        /// Currently-selected master EQ preset name (e.g. "Flat", "Bass",
        /// "Voice"). Drives the highlight on the master EQ chip strip via
        /// <c>EqPresetKeyMatchConverter</c>. Set by
        /// <see cref="ApplyMasterEqPreset"/> whenever a preset chip is clicked
        /// or the EQ is reset; users dragging individual sliders implicitly
        /// land on a "Custom" curve so we clear the highlight.
        /// </summary>
        [ObservableProperty]
        private string selectedMasterEqPresetKey = EqualizerPresets.Flat;

        /// <summary>Token bound to the master EQ preset radio group.</summary>
        public string MasterEqPresetGroupToken { get; } = "master-eq-preset";

        /// <summary>
        /// Applies a named EQ preset to the master engine. Updates every
        /// <c>MasterEqBandN</c> property (so live updates flow through the
        /// existing band-change hooks) and lights the matching chip in the
        /// strip. Safe to call from XAML <c>Click</c> handlers via
        /// <c>{Binding ApplyMasterEqPresetCommand}</c>.
        /// </summary>
        [RelayCommand]
        public void ApplyMasterEqPreset(string? key)
        {
            var canonical = EqualizerPresets.Canonical(key);
            var gains = EqualizerPresets.Get(canonical);
            // Suppress chip clearing while we set bands programmatically.
            _suppressEqPresetClear = true;
            try
            {
                SetMasterEqualizerGains(gains);
            }
            finally
            {
                _suppressEqPresetClear = false;
            }
            SelectedMasterEqPresetKey = canonical;
        }

        // Set during preset application so the band PropertyChanged handlers
        // know not to drop SelectedMasterEqPresetKey to "Custom" — the bands
        // are moving because a preset is being installed, not because the
        // user dragged a slider.
        private bool _suppressEqPresetClear;

        partial void OnMasterEqBand0Changed(float value) => ApplyMasterEqLive();
        partial void OnMasterEqBand1Changed(float value) => ApplyMasterEqLive();
        partial void OnMasterEqBand2Changed(float value) => ApplyMasterEqLive();
        partial void OnMasterEqBand3Changed(float value) => ApplyMasterEqLive();
        partial void OnMasterEqBand4Changed(float value) => ApplyMasterEqLive();
        partial void OnMasterEqBand5Changed(float value) => ApplyMasterEqLive();
        partial void OnMasterEqBand6Changed(float value) => ApplyMasterEqLive();
        partial void OnMasterEqBand7Changed(float value) => ApplyMasterEqLive();
        partial void OnMasterEqBand8Changed(float value) => ApplyMasterEqLive();
        partial void OnMasterEqBand9Changed(float value) => ApplyMasterEqLive();

        private void ApplyMasterEqLive()
        {
            var gains = GetMasterEqualizerGains();
            UserSettings.SetMasterEqualizerGains(gains);
            if (BassEngine.IsBridgeRunning)
                BassEngine.UpdateBridgeEqualizer(gains);

            // NAudio duplication (any tap): drive every open session from the master EQ strip.
            DuplicationManager.UpdateEqualizerMirrorSessions(gains);

            // The user dragged a slider, so the curve is no longer a known
            // preset. Clear the highlight unless a preset application is
            // explicitly setting these bands right now.
            if (!_suppressEqPresetClear && SelectedMasterEqPresetKey != "Custom")
                SelectedMasterEqPresetKey = "Custom";
        }

        private void ApplyMasterSpatialLive()
        {
            if (BassEngine.IsBridgeRunning)
            {
                var bus = BassEngine.BridgeSourceId;
                if (!string.IsNullOrWhiteSpace(bus))
                    BassEngine.SetSpatialPreset(bus, MasterSpatialPreset);
            }

            DuplicationManager.UpdateSpatialMirrorSessions(MasterSpatialPreset);
        }

        // ── Master engine orchestration ──────────────────────────────────────
        // Triggered whenever Power flips state, the bus device changes, or a
        // physical output toggles its IsActiveOutput chip. The orchestrator
        // either tears the bridge down or (re)builds it from current state.
        // Intentionally synchronous from the UI thread; BASS is already
        // thread-safe and StartBridge is fast enough not to block the UI on
        // a typical setup.
        private readonly object _masterBridgeGate = new();
        private bool _autoEngageAttempted;

        private void OnPowerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PowerService.IsActive)
                || e.PropertyName == nameof(PowerService.BusDevice)
                || e.PropertyName == nameof(PowerService.IsBackupModeActive))
            {
                RebuildMasterBridge();
                UpdateTotalLatencyBanner();
            }
        }

        /// <summary>
        /// Loopback tap for backup mode when VB-CABLE is missing: WASAPI captures
        /// whatever Windows is currently playing through the Multimedia default.
        /// </summary>
        private static string? TryGetDefaultMultimediaPlaybackId()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return device?.ID;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryGetDefaultMultimediaPlaybackId: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Bridge capture endpoint: VB-CABLE / SonicFlow when installed, otherwise
        /// the live Windows default playback device ID (backup loopback tap).
        /// </summary>
        private string? ResolveMasterBridgeCaptureId()
        {
            var busId = Power.BusDevice?.Id;
            if (!string.IsNullOrWhiteSpace(busId))
                return busId;

            if (Power.IsActive && Power.IsBackupModeActive)
                return TryGetDefaultMultimediaPlaybackId();

            return null;
        }

        /// <summary>
        /// Idempotent (re)build of the master BASS bridge based on current
        /// <see cref="PowerService"/> state, the active bus device, and the
        /// list of physical outputs marked <c>IsActiveOutput=true</c>.
        ///
        /// Safe to call from any property change handler — internal lock
        /// serialises concurrent rebuilds, and BASS sees one teardown +
        /// one start per call regardless of how many properties moved.
        /// </summary>
        public void RebuildMasterBridge()
        {
            try
            {
                lock (_masterBridgeGate)
                {
                    var bridgeSourceId = ResolveMasterBridgeCaptureId();
                    bool shouldEngage = Power.IsActive && !string.IsNullOrWhiteSpace(bridgeSourceId);
                    if (!shouldEngage)
                    {
                        if (BassEngine.IsBridgeRunning)
                            BassEngine.StopBridge();
                        return;
                    }

                    // Fan-out to every mirrored endpoint that is flagged Active.
                    // Use the full Devices list (not PhysicalDevices — virtual sinks
                    // like Voicemeeter cables are wrongly excluded there) while
                    // always stripping the bridge source so we never echo the tap
                    // back onto itself.
                    var activeTargets = Devices
                        .Where(d => d.IsActiveOutput
                                    && !string.IsNullOrWhiteSpace(d.Id)
                                    && !string.Equals(d.Id, bridgeSourceId, StringComparison.OrdinalIgnoreCase))
                        .Select(d => d.Id!)
                        .ToList();

                    if (activeTargets.Count == 0)
                    {
                        if (BassEngine.IsBridgeRunning)
                            BassEngine.StopBridge();
                        return;
                    }

                    var gains = GetMasterEqualizerGains();
                    bool started = BassEngine.StartBridge(bridgeSourceId!, activeTargets, gains);
                    if (started)
                    {
                        BassEngine.SetSpatialPreset(bridgeSourceId!, MasterSpatialPreset);

                        // Push the per-device sync offsets so freshly added
                        // BT / HDMI targets get sensible defaults right away.
                        foreach (var device in Devices)
                        {
                            if (!device.IsActiveOutput) continue;
                            if (string.IsNullOrWhiteSpace(device.Id)
                                || string.Equals(device.Id, bridgeSourceId, StringComparison.OrdinalIgnoreCase))
                                continue;
                            BassEngine.UpdateBridgeTargetLatency(device.Id!, device.TargetLatencyOffsetMs);
                        }

                        UpdateTotalLatencyBanner();
                    }
                    else
                    {
                        Debug.WriteLine("RebuildMasterBridge: StartBridge returned false");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RebuildMasterBridge failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Approximate end-to-end latency the user is currently experiencing
        /// across active outputs. Surfaced in the bus chip tooltip so the
        /// number changes live as the user moves any sync slider. Formula:
        /// baseline buffer (~50ms WASAPI loopback) + max active target offset.
        /// </summary>
        [ObservableProperty]
        private string totalLatencyBanner = "≈ 50 ms total";

        private void UpdateTotalLatencyBanner()
        {
            if (!Power.IsActive)
            {
                TotalLatencyBanner = "Bus is off";
                return;
            }

            const int wasapiBaselineMs = 50;
            int maxOffset = 0;
            foreach (var device in Devices)
            {
                if (device.IsActiveOutput && device.TargetLatencyOffsetMs > maxOffset)
                    maxOffset = device.TargetLatencyOffsetMs;
            }
            int total = wasapiBaselineMs + maxOffset;

            if (!BassEngine.IsBridgeRunning)
            {
                TotalLatencyBanner = Power.IsBackupModeActive
                    ? "Backup: mark other outputs Active (or install VB-CABLE)."
                    : "Turn on Active on at least one output destination.";
                return;
            }

            TotalLatencyBanner = $"≈ {total} ms total";
        }

        /// <summary>
        /// Hook called from physical-device cards when the user toggles
        /// <see cref="AudioDevice.IsActiveOutput"/>. Wired by
        /// <see cref="ConfigureDeviceDuplicateTargets"/> after each
        /// <see cref="RefreshData"/>. The IsActiveOutput change comes through
        /// <see cref="OnOutputDevicePropertyChanged"/> below.
        /// </summary>
        private void OnPhysicalActiveToggled(AudioDevice device)
        {
            if (Power.IsActive)
                RebuildMasterBridge();
        }

        /// <summary>Bound for empty-state UI when no apps expose an audio session.</summary>
        public bool HasActiveSessions => Sessions.Count > 0;

        /// <summary>Bound for empty-state UI on the Recording page.</summary>
        public bool HasAnyMicrophones => Microphones.Count > 0;

        partial void OnSessionsChanged(ObservableCollection<AppAudioSession>? oldValue, ObservableCollection<AppAudioSession> newValue)
            => OnPropertyChanged(nameof(HasActiveSessions));

        partial void OnMicrophonesChanged(ObservableCollection<AudioDevice>? oldValue, ObservableCollection<AudioDevice> newValue)
            => OnPropertyChanged(nameof(HasAnyMicrophones));

        private readonly Dictionary<string, SemaphoreSlim> _duplicateLocks = new();

        /// <summary>
        /// Single-click power toggle for the SonicFlow audio bus. Used by the
        /// header power chip and the system-tray menu so both surfaces drive
        /// the same state machine. Implemented as synchronous command + detached
        /// task so Toolkit's AsyncRelayCommand never leaves the Button disabled
        /// after one await (users thought the chip was permanently stuck On).
        /// </summary>
        [RelayCommand]
        private void TogglePower() => _ = TogglePowerCoreAsync();

        private async Task TogglePowerCoreAsync()
        {
            try
            {
                if (Power.IsActive)
                    await Power.DisengageAsync();
                else
                    await Power.EngageAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TogglePower failed: {ex.Message}");
            }
        }

        [RelayCommand]
        public void ToggleTestTone()
        {
            if (IsTestTonePlaying)
            {
                StopTestTone();
            }
            else
            {
                PlayTestTone();
            }
        }

        private void PlayTestTone()
        {
            try
            {
                if (MasterDevice?.Id == null)
                {
                    MessageBox.Show("Please select an output device first.", "Notice", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                StopTestTone();

                bool ok = BassEngine.PlayTestTone(MasterDevice.Id);
                if (ok)
                {
                    IsTestTonePlaying = true;
                }
                else
                {
                    // Check if DLLs are actually there first
                    if (!BassEngine.CheckNativeDlls(out string missing))
                    {
                        MessageBox.Show($"BASS Engine files missing: {missing}\n\nPlease ensure bass.dll, bassmix.dll, and bass_fx.dll are in the 'Native' folder.", "Engine Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    else
                    {
                        MessageBox.Show("The audio engine failed to start on this device.\n\nCheck if the device is currently in use or try another output.", "Audio Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PlayTestTone error: {ex.Message}");
                StopTestTone();
            }
        }

        private void StopTestTone()
        {
            IsTestTonePlaying = false;
            BassEngine.StopTestTone();
        }

        public void RefreshMeters()
        {
            SessionManager.UpdateSessionLevels(Sessions);
            DeviceManager.UpdateDeviceLevels(Devices);
            DeviceManager.UpdateDeviceLevels(Microphones);

            // Duplicate-to targets (NAudio sessions) always follow master EQ even when
            // duplicated from Applications (session flag) rather than Outputs (IsDuplicating).
            DuplicationManager.UpdateEqualizerMirrorSessions(GetMasterEqualizerGains());

            foreach (var device in Devices)
            {
                if (string.IsNullOrWhiteSpace(device.Id))
                    continue;
                if (DuplicationManager.IsDuplicating(device.Id))
                    continue;

                BassEngine.UpdateEqualizer(device.Id, device.GetEqualizerGains());
            }
        }

        [RelayCommand]
        public void UpdateMasterVolume(double value)
        {
            if (MasterDevice?.Id != null)
                DeviceManager.SetMasterVolume(MasterDevice.Id, (float)value);
        }

        [RelayCommand]
        public void SetDefaultDevice(AudioDevice device)
        {
            if (device?.Id == null) return;
            bool ok = AudioRouterNative.SetSystemDefaultDevice(device.Id);
            if (ok)
                RefreshData();
            else
                MessageBox.Show($"Could not set {device.Name} as default.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        [RelayCommand]
        public void SetSonicFlowVirtualDefault()
        {
            if (SonicFlowVirtualAudio.TrySetAsDefault(SonicFlowVirtualDevice, out var status))
            {
                SonicFlowVirtualStatus = status;
                RefreshData();
                return;
            }

            SonicFlowVirtualStatus = status;
            MessageBox.Show(status, "SonicFlow Virtual Audio", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>L2 perceptual sync wizard — docs/LATENCY_PLAN.md.</summary>
        [RelayCommand]
        public void OpenSyncCalibrationWizard()
        {
            if (!SyncCalibrationPlanner.TryPrepare(Devices.ToList(), Power.IsBackupModeActive, out var error, out var refId, out var targets))
            {
                MessageBox.Show(error, "Sync calibration", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var win = new SyncCalibrationWindow(refId!, targets);
            if (Application.Current?.MainWindow is Window owner)
                win.Owner = owner;
            win.ShowDialog();
            L2CalibrationStaleHints.Refresh(Devices.ToList());
        }

        /// <summary>L3 mic-based auto-sync wizard — docs/LATENCY_PLAN.md.</summary>
        [RelayCommand]
        public void OpenAutoSyncWizard()
        {
            var physicalActive = Devices
                .Where(d => d is { IsActiveOutput: true, IsSonicFlowVirtual: false } && !string.IsNullOrWhiteSpace(d.Id))
                .Select(d => new AutoSyncRunner.Target(d.Id!, d.Name ?? "(unnamed)"))
                .ToList();

            if (physicalActive.Count == 0)
            {
                MessageBox.Show(
                    "Mark at least one hardware output as ACTIVE before running auto-sync.",
                    "Auto-sync", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var win = new AutoSyncWindow(physicalActive);
            if (Application.Current?.MainWindow is Window owner)
                win.Owner = owner;

            bool? ok = win.ShowDialog();
            if (ok != true || win.AppliedResult is null) return;

            foreach (var t in win.AppliedResult.Targets)
            {
                if (!t.Accepted) continue;
                var dev = Devices.FirstOrDefault(d => string.Equals(d.Id, t.DeviceId, StringComparison.OrdinalIgnoreCase));
                if (dev is null) continue;

                // Setting TargetLatencyOffsetMs cascades to BassEngine.UpdateBridgeTargetLatency
                // and UserSettings.SetTargetLatencyOffset via the existing PropertyChanged hook.
                dev.TargetLatencyOffsetMs = t.NormalisedOffsetMs;

                string? fingerprint = null;
                if (SyncClickPlayer.TryCaptureFingerprint(t.DeviceId, out var fp))
                    fingerprint = fp;
                UserSettings.SetL3AutoSync(t.DeviceId, t.NormalisedOffsetMs, t.SnrDb, fingerprint);
            }

            L3AutoSyncCaption.Refresh(Devices.ToList());
        }

        [RelayCommand]
        public void RefreshData()
        {
            try
            {
                var previousDuplicateSelections = Devices
                    .Where(d => !string.IsNullOrWhiteSpace(d.Id))
                    .ToDictionary(
                        d => d.Id!,
                        d => d.DuplicateTargets
                            .Where(t => t.IsSelected && !string.IsNullOrWhiteSpace(t.Device.Id))
                            .Select(t => t.Device.Id!)
                            .ToHashSet());
                var previousEqualizerSettings = Devices
                    .Where(d => !string.IsNullOrWhiteSpace(d.Id))
                    .ToDictionary(
                        d => d.Id!,
                        d => d.GetEqualizerGains());
                var previousAdvancedExpanded = Devices
                    .Where(d => !string.IsNullOrWhiteSpace(d.Id))
                    .ToDictionary(d => d.Id!, d => d.IsAdvancedExpanded);
                var previousLatencyOffsets = Devices
                    .Where(d => !string.IsNullOrWhiteSpace(d.Id))
                    .ToDictionary(
                        d => d.Id!,
                        d => d.DuplicateTargets
                            .Where(t => !string.IsNullOrWhiteSpace(t.Device.Id))
                            .ToDictionary(t => t.Device.Id!, t => t.LatencyOffsetMs));
                var previousSourceLatencies = Devices
                    .Where(d => !string.IsNullOrWhiteSpace(d.Id))
                    .ToDictionary(d => d.Id!, d => d.SourceLatencyMs);

                var availableDevices = SonicFlowVirtualAudio.SortVirtualFirst(DeviceManager.GetRenderDevices()).ToList();
                var availableMicrophones = DeviceManager.GetCaptureDevices();
                ConfigureDeviceDuplicateTargets(availableDevices, previousDuplicateSelections, previousEqualizerSettings, previousAdvancedExpanded, previousLatencyOffsets, previousSourceLatencies);
                
                Devices = new ObservableCollection<AudioDevice>(availableDevices);
                Power.RefreshBus(availableDevices);

                var vDevices = availableDevices.Where(d => d.IsSonicFlowVirtual).ToList();
                // Profiles are an implementation detail in v1 — see docs/AUDIO_BUS_PLAN.md.
                // Keep the property in case any binding still consults it, but stop
                // displaying it as "Profile N" in the UI; the bus is invisible plumbing.
                for (int i = 0; i < vDevices.Count; i++)
                {
                    vDevices[i].ProfileLabel = null;
                }
                
                VirtualDevices = new ObservableCollection<AudioDevice>(vDevices);
                PhysicalDevices = new ObservableCollection<AudioDevice>(availableDevices.Where(d => !d.IsSonicFlowVirtual));
                Microphones = new ObservableCollection<AudioDevice>(availableMicrophones);

                L2CalibrationStaleHints.Refresh(availableDevices);
                L3AutoSyncCaption.Refresh(availableDevices);
                
                var defaultDevice = Devices.FirstOrDefault(d => d.IsDefault);
                SonicFlowVirtualDevice = SonicFlowVirtualAudio.FindVirtualRenderDevice(availableDevices);
                IsSonicFlowVirtualDeviceInstalled = VirtualDevices.Any();
                SonicFlowVirtualDeviceName = SonicFlowVirtualDevice?.Name ?? SonicFlowVirtualAudio.ProductRenderName;
                SonicFlowVirtualStatus = SonicFlowVirtualAudio.BuildStatus(SonicFlowVirtualDevice);

                var activeSessions = SessionManager.GetActiveSessions();
                foreach (var s in activeSessions)
                {
                    s.SelectedTargetDevice = defaultDevice;
                    foreach (var d in availableDevices)
                        s.TargetDevices.Add(new DeviceSelection { Device = d, IsSelected = false });
                }

                Sessions = new ObservableCollection<AppAudioSession>(activeSessions);
                MasterDevice = defaultDevice;

                // Auto-engage on first refresh if the user had the bus on at
                // last close. PowerService.EngageAsync is idempotent — if it
                // ran already (e.g. the user clicked Power between launches)
                // this collapses to a no-op.
                if (!_autoEngageAttempted)
                {
                    _autoEngageAttempted = true;
                    // Engage restores backup mode when VB-CABLE is missing — do not gate on BusDevice.
                    if (UserSettings.GetWasPoweredOnAtClose() && !Power.IsActive)
                    {
                        _ = Power.EngageAsync();
                    }
                }

                RebuildMasterBridge();
                UpdateTotalLatencyBanner();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RefreshData error: {ex.Message}");
            }
        }

        private void ConfigureDeviceDuplicateTargets(
            IReadOnlyList<AudioDevice> devices,
            IReadOnlyDictionary<string, HashSet<string>>? previousSelections = null,
            IReadOnlyDictionary<string, float[]>? previousEqualizerSettings = null,
            IReadOnlyDictionary<string, bool>? previousAdvancedExpanded = null,
            IReadOnlyDictionary<string, Dictionary<string, int>>? previousLatencyOffsets = null,
            IReadOnlyDictionary<string, int>? previousSourceLatencies = null)
        {
            bool hasSonicFlowVirtualDevice = devices.Any(d => d.IsSonicFlowVirtual);

            foreach (var source in devices)
            {
                HashSet<string>? restoredTargetIds = null;
                float[]? equalizerValues = null;
                Dictionary<string, int>? latencyMap = null;
                int sourceLatency = 0;
                previousSelections?.TryGetValue(source.Id ?? string.Empty, out restoredTargetIds);
                previousEqualizerSettings?.TryGetValue(source.Id ?? string.Empty, out equalizerValues);
                previousLatencyOffsets?.TryGetValue(source.Id ?? string.Empty, out latencyMap);
                previousSourceLatencies?.TryGetValue(source.Id ?? string.Empty, out sourceLatency);
                source.SourceLatencyMs = sourceLatency;
                source.CanHostMirroring = !hasSonicFlowVirtualDevice || source.IsSonicFlowVirtual;

                bool wasExpanded = false;
                if (!string.IsNullOrWhiteSpace(source.Id))
                {
                    if (previousAdvancedExpanded != null
                        && previousAdvancedExpanded.TryGetValue(source.Id!, out var prevExpanded))
                        wasExpanded = prevExpanded;
                    else
                        wasExpanded = UserSettings.GetAdvancedExpanded(source.Id!);
                }

                var targetSelections = source.CanHostMirroring
                    ? devices
                        .Where(target => target.Id != source.Id && (!source.IsSonicFlowVirtual || !target.IsSonicFlowVirtual))
                        .Select(target =>
                        {
                            var selection = new DeviceSelection
                            {
                                Device = target,
                                IsSelected = restoredTargetIds?.Contains(target.Id ?? string.Empty) == true
                            };
                            if (latencyMap != null && target.Id != null && latencyMap.TryGetValue(target.Id, out var ms))
                                selection.LatencyOffsetMs = ms;
                            return selection;
                        })
                    : Enumerable.Empty<DeviceSelection>();

                source.DuplicateTargets = new ObservableCollection<DeviceSelection>(targetSelections);

                if (equalizerValues != null && equalizerValues.Length > 0)
                    source.SetEqualizerGains(equalizerValues);

                foreach (var target in source.DuplicateTargets)
                    target.PropertyChanged += (_, e) => OnDuplicateTargetSelectionChanged(source, target, e);

                source.PropertyChanged += (_, e) => OnOutputDevicePropertyChanged(source, e);

                // Restore persisted spatial preset. Done after the PropertyChanged
                // handler is wired so the engine + save path runs naturally; the
                // re-save is value-equal so it's a no-op write at worst.
                if (!string.IsNullOrWhiteSpace(source.Id))
                {
                    source.SpatialPreset = UserSettings.GetSpatialPreset(source.Id);
                    
                    var savedGains = UserSettings.GetEqualizerGains(source.Id);
                    if (savedGains != null && savedGains.Length == 10)
                        source.SetEqualizerGains(savedGains);
                }

                source.IsDuplicating = DuplicationManager.IsDuplicating(source.Id);
                source.IsAdvancedExpanded = wasExpanded;
                UpdateDuplicateStatus(source);
            }
        }

        private SemaphoreSlim GetDuplicateLock(string deviceId)
        {
            lock (_duplicateLocks)
            {
                if (!_duplicateLocks.TryGetValue(deviceId, out var gate))
                {
                    gate = new SemaphoreSlim(1, 1);
                    _duplicateLocks[deviceId] = gate;
                }

                return gate;
            }
        }

        private void OnOutputDevicePropertyChanged(AudioDevice sourceDevice, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AudioDevice.EqBand0)
                or nameof(AudioDevice.EqBand1)
                or nameof(AudioDevice.EqBand2)
                or nameof(AudioDevice.EqBand3)
                or nameof(AudioDevice.EqBand4)
                or nameof(AudioDevice.EqBand5)
                or nameof(AudioDevice.EqBand6)
                or nameof(AudioDevice.EqBand7)
                or nameof(AudioDevice.EqBand8)
                or nameof(AudioDevice.EqBand9))
            {
                var gains = sourceDevice.GetEqualizerGains();

                // Hardware outputs use master-bridge DSP only — never BassEngine.UpdateEqualizer per device id.
                if (!string.IsNullOrWhiteSpace(sourceDevice.Id))
                {
                    if (DuplicationManager.IsDuplicating(sourceDevice.Id))
                        DuplicationManager.UpdateEqualizer(sourceDevice.Id, GetMasterEqualizerGains());
                    else if (sourceDevice.IsSonicFlowVirtual)
                        BassEngine.UpdateEqualizer(sourceDevice.Id, gains);

                    UserSettings.SetEqualizerGains(sourceDevice.Id, gains);
                }

                return;
            }

            if (e.PropertyName == nameof(AudioDevice.SourceLatencyMs))
            {
                DuplicationManager.SetSourceLatency(sourceDevice.Id, sourceDevice.SourceLatencyMs);
                return;
            }

            if (e.PropertyName == nameof(AudioDevice.IsActiveOutput))
            {
                // Toggling a real output's [Active] chip while the master bus
                // is on rebuilds the bridge fan-out. When the bus is off this
                // is a pure UI state change with no engine work.
                OnPhysicalActiveToggled(sourceDevice);
                UpdateTotalLatencyBanner();
                return;
            }

            if (e.PropertyName == nameof(AudioDevice.TargetLatencyOffsetMs))
            {
                if (!string.IsNullOrWhiteSpace(sourceDevice.Id))
                {
                    UserSettings.SetTargetLatencyOffset(sourceDevice.Id, sourceDevice.TargetLatencyOffsetMs);
                    if (BassEngine.IsBridgeRunning && sourceDevice.IsActiveOutput)
                        BassEngine.UpdateBridgeTargetLatency(sourceDevice.Id, sourceDevice.TargetLatencyOffsetMs);
                }
                UpdateTotalLatencyBanner();
                return;
            }

            if (e.PropertyName == nameof(AudioDevice.IsAdvancedExpanded))
            {
                if (!string.IsNullOrWhiteSpace(sourceDevice.Id))
                    UserSettings.SetAdvancedExpanded(sourceDevice.Id, sourceDevice.IsAdvancedExpanded);
                return;
            }

            if (e.PropertyName == nameof(AudioDevice.SpatialPreset))
            {
                if (string.IsNullOrWhiteSpace(sourceDevice.Id))
                    return;

                if (DuplicationManager.IsDuplicating(sourceDevice.Id))
                    DuplicationManager.UpdateSpatial(sourceDevice.Id, MasterSpatialPreset);
                else if (sourceDevice.IsSonicFlowVirtual)
                    BassEngine.SetSpatialPreset(sourceDevice.Id, SpatialPreset.Off);

                UserSettings.SetSpatialPreset(sourceDevice.Id, sourceDevice.SpatialPreset);
            }
        }

        private void OnDuplicateTargetSelectionChanged(AudioDevice sourceDevice, DeviceSelection target, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DeviceSelection.IsSelected))
            {
                UpdateDuplicateStatus(sourceDevice);

                if (sourceDevice.IsDuplicating)
                    _ = ApplyDeviceDuplicateTargets(sourceDevice, false);
                return;
            }

            if (e.PropertyName == nameof(DeviceSelection.LatencyOffsetMs))
            {
                // Push live - no need to restart the stream. Only meaningful while
                // duplication is running, but storing the value either way keeps the
                // session in sync if the user later flips the target on.
                if (sourceDevice.IsDuplicating)
                    DuplicationManager.SetTargetLatency(sourceDevice.Id, target.Device.Id, target.LatencyOffsetMs);
            }
        }

        private static void UpdateDuplicateStatus(AudioDevice sourceDevice)
        {
            if (!sourceDevice.CanHostMirroring)
            {
                sourceDevice.DuplicateStatus = "Mirroring is controlled by SonicFlow Virtual Speaker";
                return;
            }

            if (sourceDevice.IsDuplicateBusy)
            {
                sourceDevice.DuplicateStatus = "Updating duplicate targets...";
                return;
            }

            int selectedCount = sourceDevice.DuplicateTargets.Count(d => d.IsSelected);
            if (sourceDevice.IsDuplicating && selectedCount > 0)
            {
                sourceDevice.DuplicateStatus = $"Mirroring to {selectedCount} device(s)";
            }
            else if (selectedCount > 0)
            {
                sourceDevice.DuplicateStatus = $"{selectedCount} target device(s) selected";
            }
            else
            {
                sourceDevice.DuplicateStatus = "No duplicate targets active";
            }
        }

        [RelayCommand]
        public void ShowOutputs() => CurrentSection = DashboardSection.Outputs;

        [RelayCommand]
        public void ShowApplications() => CurrentSection = DashboardSection.Applications;

        [RelayCommand]
        public void ShowMicrophones() => CurrentSection = DashboardSection.Microphones;

        [RelayCommand]
        public void ShowTools() => CurrentSection = DashboardSection.Tools;

        public void UpdateTargetLatency(AudioDevice sourceDevice, string targetId, int offsetMs)
        {
            if (string.IsNullOrWhiteSpace(sourceDevice.Id) || string.IsNullOrWhiteSpace(targetId)) return;

            if (sourceDevice.IsDuplicating)
                DuplicationManager.SetTargetLatency(sourceDevice.Id, targetId, offsetMs);
        }

        private void NormalizeMirrorSelectionsAfterExclusiveTakeover(string reservingSourceId, IReadOnlyList<string> stolenTargetIds)
        {
            if (stolenTargetIds.Count == 0) return;

            foreach (var d in Devices.Where(x => x.CanHostMirroring && x.Id != reservingSourceId && !string.IsNullOrWhiteSpace(x.Id)))
            {
                foreach (var sel in d.DuplicateTargets)
                {
                    string? tid = sel.Device.Id;
                    if (string.IsNullOrWhiteSpace(tid) || !stolenTargetIds.Contains(tid)) continue;

                    sel.IsSelected = false;
                }
            }

            AlignVirtualMirroringStatesWithEngine();
        }

        /// <summary>Syncs Profile 1 / Profile 2 (and similar) duplicate toggles after the native layer steals playback targets.</summary>
        private void AlignVirtualMirroringStatesWithEngine()
        {
            foreach (var d in Devices.Where(x => x.IsSonicFlowVirtual && !string.IsNullOrWhiteSpace(x.Id)))
            {
                bool engineSays = DuplicationManager.IsDuplicating(d.Id);
                if (d.IsDuplicating != engineSays)
                {
                    d.IsDuplicating = engineSays;
                    UpdateDuplicateStatus(d);
                }
            }
        }

        [RelayCommand]
        public async Task ToggleDeviceDuplicate(AudioDevice? sourceDevice)
        {
            if (sourceDevice?.Id == null) return;
            // Outputs v1: mirrored hardware is fed only via the master bridge + ACTIVE chips.
            if (!sourceDevice.IsSonicFlowVirtual)
                return;
            if (!sourceDevice.CanHostMirroring)
            {
                MessageBox.Show(
                    "Mirror targets are controlled by the SonicFlow virtual device.",
                    "Device Duplication", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (sourceDevice.IsDuplicating)
            {
                var gainsStopped = GetMasterEqualizerGains();
                await Task.Run(() => {
                    DuplicationManager.StopDuplication(sourceDevice.Id);
                    BassEngine.StopBridge();
                });
                sourceDevice.IsDuplicating = false;
                UpdateDuplicateStatus(sourceDevice);

                BassEngine.UpdateEqualizer(sourceDevice.Id, gainsStopped);
                AlignVirtualMirroringStatesWithEngine();
                return;
            }

            await ApplyDeviceDuplicateTargets(sourceDevice, true);
        }

        [RelayCommand]
        public async Task ApplyDeviceDuplicateTargets(AudioDevice? sourceDevice)
            => await ApplyDeviceDuplicateTargets(sourceDevice, true);

        private async Task ApplyDeviceDuplicateTargets(AudioDevice? sourceDevice, bool showValidationMessage)
        {
            if (sourceDevice?.Id == null) return;
            if (!sourceDevice.IsSonicFlowVirtual) return;
            if (!sourceDevice.CanHostMirroring) return;

            var gate = GetDuplicateLock(sourceDevice.Id);
            await gate.WaitAsync();
            sourceDevice.IsDuplicateBusy = true;
            UpdateDuplicateStatus(sourceDevice);

            var targetIds = sourceDevice.DuplicateTargets
                .Where(d => d.IsSelected && d.Device.Id != sourceDevice.Id && !string.IsNullOrWhiteSpace(d.Device.Id))
                .Select(d => d.Device.Id!)
                .Distinct()
                .ToList();

            try
            {
                if (targetIds.Count == 0)
                {
                    if (sourceDevice.IsDuplicating)
                    {
                        var gainsRestore = GetMasterEqualizerGains();
                        await Task.Run(() => {
                            DuplicationManager.StopDuplication(sourceDevice.Id);
                            BassEngine.StopBridge();
                        });
                        sourceDevice.IsDuplicating = false;
                        BassEngine.UpdateEqualizer(sourceDevice.Id!, gainsRestore);
                    }

                    UpdateDuplicateStatus(sourceDevice);
                    AlignVirtualMirroringStatesWithEngine();
                    if (showValidationMessage)
                    {
                        MessageBox.Show(
                            "Select at least one additional output device to mirror this source.",
                            "Device Duplication", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                sourceDevice.IsDuplicating = true;
                var latencyOffsets = sourceDevice.DuplicateTargets
                    .Where(d => d.IsSelected && !string.IsNullOrWhiteSpace(d.Device.Id))
                    .ToDictionary(d => d.Device.Id!, d => d.LatencyOffsetMs);

                int sourceLatency = sourceDevice.SourceLatencyMs;
                var gains = GetMasterEqualizerGains();

                bool started = await Task.Run(() =>
                {
                    BassEngine.StopBridge();
                    BassEngine.StopStandaloneDeviceProcessing(sourceDevice.Id!);

                    bool ok = DuplicationManager.StartDuplication(
                        sourceDevice.Id!,
                        targetIds,
                        gains,
                        MasterSpatialPreset);

                    if (ok)
                    {
                        // Source latency must be applied first so per-target SetTargetLatency
                        // computes the combined effective delay correctly.
                        DuplicationManager.SetSourceLatency(sourceDevice.Id, sourceLatency);
                        foreach (var (targetId, ms) in latencyOffsets)
                            DuplicationManager.SetTargetLatency(sourceDevice.Id, targetId, ms);
                    }

                    return ok;
                });

                if (!started)
                {
                    sourceDevice.IsDuplicating = false;
                    var reason = DuplicationManager.GetLastError(sourceDevice.Id);
                    sourceDevice.DuplicateStatus = reason ?? "Could not start duplication";
                    MessageBox.Show(
                        reason ?? "Could not start device duplication.\nMake sure audio is currently playing on the source device, then try again.",
                        "Device Duplication", MessageBoxButton.OK, MessageBoxImage.Warning);
                    AlignVirtualMirroringStatesWithEngine();
                    return;
                }

                NormalizeMirrorSelectionsAfterExclusiveTakeover(sourceDevice.Id!, targetIds);
                UpdateDuplicateStatus(sourceDevice);
                sourceDevice.IsAdvancedExpanded = true;
            }
            finally
            {
                sourceDevice.IsDuplicateBusy = false;
                UpdateDuplicateStatus(sourceDevice);
                gate.Release();
            }
        }

        // Routes the session to its selected device, then mutes+unmutes to force the app's
        // audio engine to re-open its render client on the new device immediately.
        [RelayCommand]
        public async Task RouteApp(AppAudioSession? session)
        {
            if (session == null) return;
            var device = session.SelectedTargetDevice;
            if (device?.Id == null)
            {
                MessageBox.Show("Select a device from the dropdown first.", "Route", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool ok = AudioRouterNative.SetAppDefaultDevice(
                session.ProcessId, session.ExePath, device.Id, session.ProcessName);

            // Mute → wait → unmute forces the app's audio client to close and re-open
            // on the newly persisted endpoint. More reliable than a volume nudge.
            await Task.Run(() =>
            {
                SessionManager.SetMute(session.ProcessId, true);
                System.Threading.Thread.Sleep(250);
                SessionManager.SetMute(session.ProcessId, false);
            });

            if (!ok)
                MessageBox.Show(
                    $"Routing {session.ProcessName} → {device.Name} may have failed.\n" +
                    "If audio doesn't move, try play/pause in the app.",
                    "Routing", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Single toggle: starts duplication (auto-routes first) or stops it.
        // Heavy WASAPI work runs on a background thread so the UI stays responsive.
        [RelayCommand]
        public async Task ToggleDuplicate(AppAudioSession? session)
        {
            if (session == null) return;
            var sourceDevice = session.SelectedTargetDevice;
            if (sourceDevice?.Id == null)
            {
                MessageBox.Show(
                    "Select the source device in 'Route to' before duplicating.",
                    "Duplication", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (session.IsDuplicating)
            {
                var gainsRestore = GetMasterEqualizerGains();
                await Task.Run(() =>
                {
                    DuplicationManager.StopDuplication(sourceDevice.Id);
                    BassEngine.StopBridge();
                });
                session.IsDuplicating = false;
                BassEngine.UpdateEqualizer(sourceDevice.Id, gainsRestore);
                AlignVirtualMirroringStatesWithEngine();
                return;
            }

            var targetIds = session.TargetDevices
                .Where(d => d.IsSelected && d.Device.Id != sourceDevice.Id)
                .Select(d => d.Device.Id!)
                .ToList();

            if (targetIds.Count == 0)
            {
                MessageBox.Show(
                    "Check at least one additional device in the 'Duplicate to' list.",
                    "Duplication", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Mark as duplicating immediately so the button shows "Stop" during init
            session.IsDuplicating = true;

            bool started = await Task.Run(() =>
            {
                // 1. Persist the routing for this process
                AudioRouterNative.SetAppDefaultDevice(
                    session.ProcessId, session.ExePath, sourceDevice.Id, session.ProcessName);

                // 2. Mute→unmute to force the audio client onto the source device
                SessionManager.SetMute(session.ProcessId, true);
                System.Threading.Thread.Sleep(250);
                SessionManager.SetMute(session.ProcessId, false);

                BassEngine.StopBridge();
                BassEngine.StopStandaloneDeviceProcessing(sourceDevice.Id);

                // 3. Start loopback capture from source device + fan out to targets
                //    using the same master EQ / spatial DSP as the bus card.
                return DuplicationManager.StartDuplication(
                    sourceDevice.Id,
                    targetIds,
                    GetMasterEqualizerGains(),
                    MasterSpatialPreset);
            });

            if (!started)
            {
                session.IsDuplicating = false;
                MessageBox.Show(
                    "Could not open the audio devices for duplication.\n" +
                    "Make sure audio is playing in the app, then try again.",
                    "Duplication Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                AlignVirtualMirroringStatesWithEngine();
            }
            else
            {
                NormalizeMirrorSelectionsAfterExclusiveTakeover(sourceDevice.Id!, targetIds);
                AlignVirtualMirroringStatesWithEngine();
            }
        }

        [RelayCommand]
        public void ResetApp(AppAudioSession? session)
        {
            if (session != null)
                AudioRouterNative.ResetAppDefaultDevice(session.ProcessId, session.ExePath, session.ProcessName);
        }
    }
}
