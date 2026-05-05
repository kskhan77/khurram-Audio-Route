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
                MatrixSurroundBridgeUpmix = UserSettings.GetMatrixSurroundBridgeUpmix();
                MatrixBridgeChannelOrder = UserSettings.GetMatrixBridgeChannelOrder();
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

        /// <summary>
        /// Voice studio (CABLE-B) mic chain. See <c>docs/MIC_CHAIN_PLAN.md</c>.
        /// Bound from the Microphones page Voice Studio card.
        /// </summary>
        public MicChainViewModel MicChain { get; } = new MicChainViewModel();

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

        /// <summary>
        /// Experimental: matrix stereo→5.1/7.1 on bridge targets that negotiate 48 kHz / 6 or 8 ch.
        /// See <see cref="BassEngine"/> matrix path and <c>SurroundUpmixer</c>.
        /// </summary>
        [ObservableProperty]
        private bool matrixSurroundBridgeUpmix;

        partial void OnMatrixSurroundBridgeUpmixChanged(bool value)
        {
            UserSettings.SetMatrixSurroundBridgeUpmix(value);
            RebuildMasterBridge();
        }

        /// <summary>Tools → Multichannel bridge: WASAPI 7.1 packing vs internal matrix order.</summary>
        [ObservableProperty]
        private string matrixBridgeChannelOrder = MatrixBridgeChannelReorder.OrderAuto;

        partial void OnMatrixBridgeChannelOrderChanged(string value)
        {
            UserSettings.SetMatrixBridgeChannelOrder(value ?? MatrixBridgeChannelReorder.OrderAuto);
            RebuildMasterBridge();
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
                    BassEngine.SetSpatialPreset(bus, EffectiveMasterSpatialPresetForBridge());
            }

            DuplicationManager.UpdateSpatialMirrorSessions(MasterSpatialPreset);
        }

        /// <summary>
        /// <see cref="SpatialPreset.Speakers_5_1"/> vs <see cref="SpatialPreset.Speakers_7_1"/> matrix
        /// follows the loudest multichannel layout among ACTIVE bridge targets (see <see cref="SpatialPresetSinkRouting"/>).
        /// Other presets pass through unchanged.
        /// </summary>
        private SpatialPreset EffectiveMasterSpatialPresetForBridge()
        {
            var tapId = ResolveMasterBridgeCaptureId();
            int maxCh = 2;
            foreach (var d in Devices)
            {
                if (!d.IsActiveOutput || string.IsNullOrWhiteSpace(d.Id)) continue;
                if (tapId != null && string.Equals(d.Id, tapId, StringComparison.OrdinalIgnoreCase))
                    continue;
                maxCh = Math.Max(maxCh, Math.Clamp(d.RenderChannelCount, 1, 16));
            }

            return SpatialPresetSinkRouting.ResolvePhysicalSurround(MasterSpatialPreset, maxCh);
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

        // True only while RebuildMasterBridge is executing on the current thread.
        // Used to suppress the OnOutputDevicePropertyChanged → OnPhysicalActiveToggled →
        // RebuildMasterBridge recursion that fires when RebuildMasterBridge itself
        // sets IsActiveOutput on the previous-default device. Without this guard
        // the rebuild starts the bridge, the setter triggers another rebuild that
        // tears it down and starts it again — a rapid stop/start cycle that
        // intermittently fails WASAPI device init and leaves the bus silent.
        [ThreadStatic] private static bool _isInRebuild;

        private void OnPowerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Match IsActive (the only logical signal we care about), BusDevice
            // (bus endpoint hot-plug), and IsBackupModeActive. We deliberately
            // do NOT match nameof(PowerService.State) — when State transitions
            // to Active, the [ObservableProperty] generator fires PropertyChanged
            // for the derived IsActive *first*, then for State itself, which
            // would cause two back-to-back rebuilds. The second rebuild stops
            // and re-inits the WASAPI source loopback that bridge 1 just
            // started, and BASSwasapi loopback on VB-CABLE doesn't recover —
            // the source proc never fires again and the bridge fans out
            // silence. Reacting only to IsActive collapses both events into a
            // single rebuild.
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
            bool topLevel = !_isInRebuild;
            if (topLevel) _isInRebuild = true;
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

                    // Ensure the device that was Windows default BEFORE the bus
                    // engaged is marked ACTIVE — otherwise the bridge has no
                    // targets and the user hears nothing through the same
                    // physical speaker they were just using. PowerService
                    // captures that id into UserSettings.LastWindowsDefaultDeviceId
                    // before flipping the default to the bus.
                    var previousDefaultId = UserSettings.GetLastWindowsDefaultDeviceId();
                    if (!string.IsNullOrWhiteSpace(previousDefaultId))
                    {
                        var previousDevice = Devices.FirstOrDefault(d =>
                            !string.IsNullOrWhiteSpace(d.Id)
                            && !d.IsSonicFlowVirtual
                            && string.Equals(d.Id, previousDefaultId, StringComparison.OrdinalIgnoreCase));
                        if (previousDevice is not null && !previousDevice.IsActiveOutput)
                        {
                            // Setter raises PropertyChanged → handler persists
                            // the new ACTIVE list automatically.
                            previousDevice.IsActiveOutput = true;
                        }
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
                    BassEngine.StopStandaloneDeviceProcessing(bridgeSourceId!);
                    foreach (var targetId in activeTargets)
                        BassEngine.StopStandaloneDeviceProcessing(targetId);

                    bool started = BassEngine.StartBridge(bridgeSourceId!, activeTargets, gains);
                    if (started)
                    {
                        BassEngine.SetSpatialPreset(bridgeSourceId!, EffectiveMasterSpatialPresetForBridge());

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
            finally
            {
                if (topLevel) _isInRebuild = false;
                UpdateMasterEngineStatusHint();
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

        /// <summary>Shown on Outputs — explains whether master EQ/spatial fan-out is actually running.</summary>
        [ObservableProperty]
        private string masterEngineStatusHint =
            "Audio bus OFF — master EQ and spatial only apply when the bus is ON and the engine below is LIVE.";

        /// <summary>True when <see cref="BassEngine.IsBridgeRunning"/> after the last rebuild.</summary>
        [ObservableProperty]
        private bool isMasterBridgeLive;

        private void UpdateMasterEngineStatusHint()
        {
            IsMasterBridgeLive = BassEngine.IsBridgeRunning;

            if (!Power.IsActive)
            {
                MasterEngineStatusHint =
                    "Audio bus OFF — master EQ and spatial are not in the signal path. Turn the bus ON (header or tray), then mark hardware ACTIVE.";
                return;
            }

            if (BassEngine.IsBridgeRunning)
            {
                MasterEngineStatusHint = Power.IsBackupModeActive
                    ? "Master engine LIVE (backup): processed audio only reaches other ACTIVE outputs below—the Windows default you hear directly stays dry. For full routing, install VB-CABLE and set it as default."
                    : "Master engine LIVE: apps → VB-CABLE → capture → master EQ + spatial → ACTIVE speakers/HDMI below. Listen on those ACTIVE devices; monitoring only the cable device is dry (this app does not re-inject FX back into the cable). For true 5.1/7.1 out of ACTIVE HDMI/receiver, enable Matrix surround upmix on Tools (48 kHz multichannel).";
                return;
            }

            var tap = ResolveMasterBridgeCaptureId();
            if (string.IsNullOrWhiteSpace(tap))
            {
                MasterEngineStatusHint =
                    "Bus ON but capture device unresolved — click Refresh. With VB-CABLE installed, the bus endpoint must appear in the device list.";
                return;
            }

            int activeFanOut = Devices.Count(d => d.IsActiveOutput
                                                 && !string.IsNullOrWhiteSpace(d.Id)
                                                 && !string.Equals(d.Id, tap, StringComparison.OrdinalIgnoreCase));
            if (activeFanOut == 0)
            {
                MasterEngineStatusHint =
                    "Bus ON — no fan-out: tick ACTIVE on at least one real speaker or HDMI (not the virtual cable). Without an ACTIVE destination the bridge does not start.";
                return;
            }

            MasterEngineStatusHint =
                "Bus ON but the bridge failed to start — see Debug Output for lines starting with BASS BRIDGE. Try Refresh, exit apps using exclusive audio on those outputs, or restart.";
        }

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

        /// <summary>
        /// Persists the user's current ACTIVE selection so it survives bridge
        /// rebuilds and app restarts. Called from the IsActiveOutput
        /// PropertyChanged hook.
        /// </summary>
        private void PersistActiveBridgeTargets()
        {
            try
            {
                var ids = Devices
                    .Where(d => d is { IsActiveOutput: true, IsSonicFlowVirtual: false }
                                && !string.IsNullOrWhiteSpace(d.Id))
                    .Select(d => d.Id!)
                    .ToList();
                UserSettings.SetActiveBridgeTargetIds(ids);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PersistActiveBridgeTargets failed: {ex.Message}");
            }
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
            MicChain.Pump();

            // Duplicate-to targets (NAudio sessions) always follow master EQ even when
            // duplicated from Applications (session flag) rather than Outputs (IsDuplicating).
            DuplicationManager.UpdateEqualizerMirrorSessions(GetMasterEqualizerGains());

            foreach (var device in Devices)
            {
                if (string.IsNullOrWhiteSpace(device.Id))
                    continue;
                if (DuplicationManager.IsDuplicating(device.Id))
                    continue;
                if (Power.IsActive)
                    continue;
                if (BassEngine.IsBridgeEndpoint(device.Id))
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
                MicChain.RefreshDevices();
                
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

            // Restore the user's previously-selected ACTIVE group from settings.
            // Empty + ActiveBridgeTargetsExplicit = user has chosen no devices on purpose.
            // Empty + !explicit (fresh install) = legacy default-on behaviour: pick the
            // hardware device the user was last listening on. Other devices opt-in.
            var activeIds = UserSettings.GetActiveBridgeTargetIds();
            bool activeExplicit = UserSettings.GetActiveBridgeTargetsExplicit();
            string? freshDefaultDeviceId = null;
            if (!activeExplicit)
            {
                // Prefer the last hardware default captured BEFORE any bus engage.
                // If the bus is already engaged (auto-engage on launch, or this
                // refresh fired while bus is on), the live OS default is VB-CABLE
                // and would get filtered out as virtual — leaving no ACTIVE device
                // and the bridge with zero fan-out. The persisted last-default is
                // the device the user was actually listening on.
                var lastHardwareId = UserSettings.GetLastWindowsDefaultDeviceId();
                if (!string.IsNullOrWhiteSpace(lastHardwareId)
                    && devices.Any(d => !d.IsSonicFlowVirtual
                                        && !string.IsNullOrWhiteSpace(d.Id)
                                        && string.Equals(d.Id, lastHardwareId, StringComparison.OrdinalIgnoreCase)))
                {
                    freshDefaultDeviceId = lastHardwareId;
                }
                else
                {
                    try
                    {
                        using var enumerator = new MMDeviceEnumerator();
                        using var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        string? liveId = dev.ID;
                        // Only seed from the live OS default if it points to a
                        // non-virtual device in our list. If it's the bus itself
                        // (VB-CABLE), leave freshDefaultDeviceId null and let
                        // RebuildMasterBridge fall back via LastWindowsDefaultDeviceId.
                        if (!string.IsNullOrWhiteSpace(liveId)
                            && devices.Any(d => !d.IsSonicFlowVirtual
                                                && !string.IsNullOrWhiteSpace(d.Id)
                                                && string.Equals(d.Id, liveId, StringComparison.OrdinalIgnoreCase)))
                        {
                            freshDefaultDeviceId = liveId;
                        }
                    }
                    catch { /* ignore */ }
                }
            }

            foreach (var source in devices)
            {
                // Restore IsActiveOutput from persisted set BEFORE we wire the
                // PropertyChanged handler — otherwise the restore would itself
                // trigger a save loop. Use SetProperty-skipping initial path.
                if (source.IsSonicFlowVirtual)
                {
                    source.IsActiveOutput = false;
                }
                else if (!string.IsNullOrWhiteSpace(source.Id))
                {
                    bool shouldBeActive;
                    if (activeExplicit)
                        shouldBeActive = activeIds.Contains(source.Id);
                    else
                        shouldBeActive = string.Equals(source.Id, freshDefaultDeviceId, StringComparison.OrdinalIgnoreCase);
                    source.IsActiveOutput = shouldBeActive;
                }
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
                // Persist the new ACTIVE list either way so the user's choice
                // survives restart. The setter wired in ConfigureDeviceDuplicateTargets
                // runs only after that initial restore pass, so the persist path
                // already starts from the user-toggled state.
                PersistActiveBridgeTargets();

                // If we're already inside RebuildMasterBridge (the rebuild itself
                // marked the previous-default device ACTIVE), do NOT recurse:
                // the in-flight rebuild is about to fan out to this device anyway.
                // Recursing causes a stop/start/stop/start cycle on the WASAPI
                // target that intermittently fails device init.
                if (!_isInRebuild)
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

        /// <summary>
        /// Outputs-page action: plays a 1 kHz test tone directly through the
        /// SonicFlow bridge for a few seconds. Confirms the entire path
        /// (master EQ + spatial → ACTIVE outputs → speakers) without depending
        /// on any external app routing audio through VB-CABLE. If you hear the
        /// beep on every ACTIVE device, the bridge is delivering audio.
        /// </summary>
        [RelayCommand]
        public void PlayBridgeTestTone()
        {
            if (!Power.IsActive)
            {
                Debug.WriteLine("PlayBridgeTestTone: Application is OFF");
                return;
            }
            BassEngine.PlayBridgeTestTone(2500);
        }

        /// <summary>
        /// Tools-page diagnostic: toggles a 50% volume cut on the master EQ DSP.
        /// If you can audibly hear the volume drop, our DSP attachment point is
        /// reaching the speakers (so EQ inaudibility means the BiQuad math, not
        /// the attachment). If you can't hear a drop, the DSP is attached to a
        /// channel that doesn't carry the audio you're hearing.
        /// </summary>
        [RelayCommand]
        public void ToggleEqKillTest()
        {
            // Round-trip: if currently 1.0, drop to 0.5; if anything else, restore to 1.0.
            // The current factor isn't exposed back through MainViewModel; we just
            // track our own intent.
            _eqKillTestActive = !_eqKillTestActive;
            BassEngine.SetEqTestKillFactor(_eqKillTestActive ? 0.5f : 1.0f);
        }
        private bool _eqKillTestActive;

        /// <summary>
        /// Tools-page action: dump bridge state to Debug Output so a user can
        /// paste it back when reporting an audio issue. Also tries to highlight
        /// any obvious red flags inline.
        /// </summary>
        [RelayCommand]
        public void DiagnoseAudio()
        {
            try
            {
                var dump = BassEngine.DumpBridgeDiagnostics();
                Debug.WriteLine(dump);
                Debug.WriteLine($"DIAGNOSE: master EQ gains in VM = [{string.Join(",", GetMasterEqualizerGains().Select(v => v.ToString("+0.0;-0.0;0", System.Globalization.CultureInfo.InvariantCulture)))}]");
                Debug.WriteLine($"DIAGNOSE: master spatial preset = {MasterSpatialPreset}, MasterSpatialActive = {MasterSpatialActive}");
                Debug.WriteLine($"DIAGNOSE: power state = {Power.State}, IsBackupModeActive = {Power.IsBackupModeActive}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DiagnoseAudio failed: {ex.Message}");
            }
        }

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
