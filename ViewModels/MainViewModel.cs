using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KhurramAudioRoute.Core;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace KhurramAudioRoute.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        [ObservableProperty]
        private ObservableCollection<AppAudioSession> sessions = new();

        [ObservableProperty]
        private ObservableCollection<AudioDevice> devices = new();

        [ObservableProperty]
        private AudioDevice? _masterDevice;

        public MainViewModel()
        {
            RefreshData();
        }

        public void RefreshMeters()
        {
            SessionManager.UpdateSessionLevels(Sessions);
            DeviceManager.UpdateDeviceLevels(Devices);
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
            DuplicationManager.StopAll();
            try
            {
                var availableDevices = DeviceManager.GetRenderDevices();
                Devices = new ObservableCollection<AudioDevice>(availableDevices);
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

            if (session.IsDuplicating)
            {
                await Task.Run(() => DuplicationManager.StopDuplication(session.ProcessId));
                session.IsDuplicating = false;
                return;
            }

            var sourceDevice = session.SelectedTargetDevice;
            if (sourceDevice?.Id == null)
            {
                MessageBox.Show(
                    "Select the source device in 'Route to' before duplicating.",
                    "Duplication", MessageBoxButton.OK, MessageBoxImage.Information);
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
                return DuplicationManager.StartDuplication(session.ProcessId, sourceDevice.Id, targetIds);
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
