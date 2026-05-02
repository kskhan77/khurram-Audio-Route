using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KhurramAudioRoute.Core;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
        [ObservableProperty]
        private ObservableCollection<AppAudioSession> sessions = new();

        [ObservableProperty]
        private ObservableCollection<AudioDevice> devices = new();

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

        private readonly Dictionary<string, SemaphoreSlim> _duplicateLocks = new();

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

            // Apply BASS Equalizer to all output devices in real-time
            foreach (var device in Devices)
            {
                if (!string.IsNullOrWhiteSpace(device.Id))
                {
                    BassEngine.UpdateEqualizer(device.Id, device.GetEqualizerGains());
                }
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
                Microphones = new ObservableCollection<AudioDevice>(availableMicrophones);
                var defaultDevice = Devices.FirstOrDefault(d => d.IsDefault);
                SonicFlowVirtualDevice = SonicFlowVirtualAudio.FindVirtualRenderDevice(availableDevices);
                IsSonicFlowVirtualDeviceInstalled = SonicFlowVirtualDevice != null;
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
                bool wasExpanded = false;
                Dictionary<string, int>? latencyMap = null;
                int sourceLatency = 0;
                previousSelections?.TryGetValue(source.Id ?? string.Empty, out restoredTargetIds);
                previousEqualizerSettings?.TryGetValue(source.Id ?? string.Empty, out equalizerValues);
                previousAdvancedExpanded?.TryGetValue(source.Id ?? string.Empty, out wasExpanded);
                previousLatencyOffsets?.TryGetValue(source.Id ?? string.Empty, out latencyMap);
                previousSourceLatencies?.TryGetValue(source.Id ?? string.Empty, out sourceLatency);
                source.SourceLatencyMs = sourceLatency;
                source.CanHostMirroring = !hasSonicFlowVirtualDevice || source.IsSonicFlowVirtual;

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
                    source.SpatialPreset = UserSettings.GetSpatialPreset(source.Id);

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

                // Update both paths immediately so the slider reacts instantly instead
                // of waiting for the 220 ms RefreshMeters tick to push gains into BASS.
                if (!string.IsNullOrWhiteSpace(sourceDevice.Id))
                    BassEngine.UpdateEqualizer(sourceDevice.Id, gains);

                DuplicationManager.UpdateEqualizer(sourceDevice.Id, gains);
                return;
            }

            if (e.PropertyName == nameof(AudioDevice.SourceLatencyMs))
            {
                DuplicationManager.SetSourceLatency(sourceDevice.Id, sourceDevice.SourceLatencyMs);
                return;
            }

            if (e.PropertyName == nameof(AudioDevice.SpatialPreset))
            {
                if (!string.IsNullOrWhiteSpace(sourceDevice.Id))
                {
                    BassEngine.SetSpatialPreset(sourceDevice.Id, sourceDevice.SpatialPreset);
                    UserSettings.SetSpatialPreset(sourceDevice.Id, sourceDevice.SpatialPreset);
                }
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

        [RelayCommand]
        public async Task ToggleDeviceDuplicate(AudioDevice? sourceDevice)
        {
            if (sourceDevice?.Id == null) return;
            if (!sourceDevice.CanHostMirroring)
            {
                MessageBox.Show(
                    "Mirror targets are controlled by the SonicFlow virtual device.",
                    "Device Duplication", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (sourceDevice.IsDuplicating)
            {
                await Task.Run(() => DuplicationManager.StopDuplication(sourceDevice.Id));
                sourceDevice.IsDuplicating = false;
                UpdateDuplicateStatus(sourceDevice);
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
                        await Task.Run(() => DuplicationManager.StopDuplication(sourceDevice.Id));
                        sourceDevice.IsDuplicating = false;
                    }

                    UpdateDuplicateStatus(sourceDevice);
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
                bool started = await Task.Run(() =>
                {
                    bool ok = DuplicationManager.StartDuplication(sourceDevice.Id, targetIds, sourceDevice.GetEqualizerGains());
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
                    return;
                }

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
                await Task.Run(() => DuplicationManager.StopDuplication(sourceDevice.Id));
                session.IsDuplicating = false;
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

                // 3. Start loopback capture from source device + fan out to targets
                //    (DuplicationSession.Start has its own settling delay + retry logic)
                return DuplicationManager.StartDuplication(sourceDevice.Id, targetIds);
            });

            if (!started)
            {
                session.IsDuplicating = false;
                MessageBox.Show(
                    "Could not open the audio devices for duplication.\n" +
                    "Make sure audio is playing in the app, then try again.",
                    "Duplication Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
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
