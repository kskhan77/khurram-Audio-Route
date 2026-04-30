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
        private DashboardSection currentSection = DashboardSection.Outputs;

        private readonly Dictionary<string, SemaphoreSlim> _duplicateLocks = new();

        public MainViewModel()
        {
            RefreshData();
        }

        public void RefreshMeters()
        {
            SessionManager.UpdateSessionLevels(Sessions);
            DeviceManager.UpdateDeviceLevels(Devices);
            DeviceManager.UpdateDeviceLevels(Microphones);
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

                var availableDevices = DeviceManager.GetRenderDevices();
                var availableMicrophones = DeviceManager.GetCaptureDevices();
                ConfigureDeviceDuplicateTargets(availableDevices, previousDuplicateSelections, previousEqualizerSettings, previousAdvancedExpanded);
                Devices = new ObservableCollection<AudioDevice>(availableDevices);
                Microphones = new ObservableCollection<AudioDevice>(availableMicrophones);
                var defaultDevice = Devices.FirstOrDefault(d => d.IsDefault);

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
            IReadOnlyDictionary<string, bool>? previousAdvancedExpanded = null)
        {
            foreach (var source in devices)
            {
                HashSet<string>? restoredTargetIds = null;
                float[]? equalizerValues = null;
                bool wasExpanded = false;
                previousSelections?.TryGetValue(source.Id ?? string.Empty, out restoredTargetIds);
                previousEqualizerSettings?.TryGetValue(source.Id ?? string.Empty, out equalizerValues);
                previousAdvancedExpanded?.TryGetValue(source.Id ?? string.Empty, out wasExpanded);

                source.DuplicateTargets = new ObservableCollection<DeviceSelection>(
                    devices
                        .Where(target => target.Id != source.Id)
                        .Select(target => new DeviceSelection
                        {
                            Device = target,
                            IsSelected = restoredTargetIds?.Contains(target.Id ?? string.Empty) == true
                        }));

                if (equalizerValues != null && equalizerValues.Length >= 5)
                {
                    source.EqLow = equalizerValues[0];
                    source.EqLowMid = equalizerValues[1];
                    source.EqMid = equalizerValues[2];
                    source.EqHighMid = equalizerValues[3];
                    source.EqHigh = equalizerValues[4];
                }

                foreach (var target in source.DuplicateTargets)
                    target.PropertyChanged += (_, e) => OnDuplicateTargetSelectionChanged(source, e);

                source.PropertyChanged += (_, e) => OnOutputDevicePropertyChanged(source, e);

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
            if (e.PropertyName is nameof(AudioDevice.EqLow)
                or nameof(AudioDevice.EqLowMid)
                or nameof(AudioDevice.EqMid)
                or nameof(AudioDevice.EqHighMid)
                or nameof(AudioDevice.EqHigh))
            {
                DuplicationManager.UpdateEqualizer(sourceDevice.Id, sourceDevice.GetEqualizerGains());
            }
        }

        private void OnDuplicateTargetSelectionChanged(AudioDevice sourceDevice, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(DeviceSelection.IsSelected))
                return;

            UpdateDuplicateStatus(sourceDevice);

            if (sourceDevice.IsDuplicating)
                _ = ApplyDeviceDuplicateTargets(sourceDevice, false);
        }

        private static void UpdateDuplicateStatus(AudioDevice sourceDevice)
        {
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
                bool started = await Task.Run(() => DuplicationManager.StartDuplication(sourceDevice.Id, targetIds, sourceDevice.GetEqualizerGains()));

                if (!started)
                {
                    sourceDevice.IsDuplicating = false;
                    sourceDevice.DuplicateStatus = "Could not start duplication";
                    MessageBox.Show(
                        "Could not start device duplication.\nMake sure audio is currently playing on the source device, then try again.",
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
