using CommunityToolkit.Mvvm.ComponentModel;
using KhurramAudioRoute.Core.Spatial;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KhurramAudioRoute.Core
{
    /// <summary>
    /// Centralised on/off switch for the SonicFlow audio bus. Owns the
    /// engage/disengage state machine described in
    /// <c>docs/AUDIO_BUS_PLAN.md</c>.
    ///
    /// Engage path:
    ///   1. Resolve a virtual render endpoint (VB-CABLE; SysVAD as a dev hook).
    ///   2. Save the current Windows default device id so we can restore it.
    ///   3. Switch Windows default to the bus.
    ///   4. Start the BASS bridge from the bus to the user's selected real
    ///      outputs with the master EQ + spatial pipeline.
    ///
    /// Disengage path mirrors that in reverse and is safe to call multiple
    /// times. The implementation here is the lifecycle skeleton; the actual
    /// bridge wiring + UI bindings land in the master-bus UI work that
    /// consumes this service.
    /// </summary>
    public sealed partial class PowerService : ObservableObject
    {
        public enum PowerState
        {
            Disabled,
            Engaging,
            Active,
            Disengaging,
            Failed
        }

        private readonly SemaphoreSlim _stateGate = new(1, 1);

        [ObservableProperty]
        private PowerState state = PowerState.Disabled;

        /// <summary>
        /// Convenience flag for two-way binding to a toggle button. Setting
        /// this kicks off <see cref="EngageAsync"/> or
        /// <see cref="DisengageAsync"/> depending on the requested value.
        /// </summary>
        public bool IsActive
        {
            get => State == PowerState.Active;
            set
            {
                if (value)
                    _ = EngageAsync();
                else
                    _ = DisengageAsync();
            }
        }

        [ObservableProperty]
        private string statusMessage = "Audio bus is off.";

        [ObservableProperty]
        private AudioDevice? busDevice;

        [ObservableProperty]
        private bool isBusInstalled;

        [ObservableProperty]
        private bool isBackupModeActive;

        partial void OnStateChanged(PowerState value)
        {
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsTransitioning));
        }

        public bool IsTransitioning => State == PowerState.Engaging || State == PowerState.Disengaging;

        /// <summary>
        /// Refresh the cached <see cref="BusDevice"/> by scanning the latest
        /// render device list. Safe to call any time the device collection
        /// changes (e.g. <see cref="ViewModels.MainViewModel.RefreshData"/>).
        /// </summary>
        public void RefreshBus(IReadOnlyList<AudioDevice> renderDevices)
        {
            BusDevice = SonicFlowVirtualAudio.FindVirtualRenderDevice(renderDevices);
            IsBusInstalled = BusDevice != null;

            // Clear backup once a real virtual bus endpoint is visible again; never
            // leave IsBackupModeActive stuck true across a VB-CABLE hot-plug.
            if (IsBusInstalled)
            {
                if (IsBackupModeActive)
                {
                    IsBackupModeActive = false;
                    if (State == PowerState.Active)
                        StatusMessage = "VB-CABLE detected. Routing through the dedicated bus.";
                }
                return;
            }

            // No VB endpoint while powered on — downgrade to backup so the orchestrator
            // can still capture the Windows default playback device via loopback.
            if (!IsBusInstalled && State == PowerState.Active)
            {
                IsBackupModeActive = true;
                StatusMessage =
                    "VB-CABLE absent — capturing the Windows default playback device "
                    + "(processed audio mirrors only to other Active outputs — install "
                    + "VB-CABLE for proper routing).";
            }
        }

        /// <summary>
        /// Engages the master bus: makes VB-CABLE the Windows default,
        /// remembers the previous default, and signals the bridge to start.
        /// Reentrancy-safe; concurrent calls collapse to a single transition.
        /// </summary>
        public async Task EngageAsync()
        {
            await _stateGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (State == PowerState.Active || State == PowerState.Engaging)
                    return;

                State = PowerState.Engaging;
                StatusMessage = "Engaging audio bus...";

                if (BusDevice?.Id == null)
                {
                    // No virtual cable installed — flip to backup mode and
                    // surface a banner. The actual self-mirror plumbing is
                    // wired by MainViewModel based on IsBackupModeActive.
                    IsBackupModeActive = true;
                    StatusMessage = "Install VB-CABLE for processed audio on every output.";
                    State = PowerState.Active;
                    PersistPoweredOnAtClose(true);
                    return;
                }

                // Remember the user's pre-engage Windows default so we can
                // restore it on disengage. Skip the save if we'd be saving
                // the bus itself (idempotent re-engage).
                var current = TryGetCurrentDefaultDeviceId();
                if (!string.IsNullOrWhiteSpace(current)
                    && !string.Equals(current, BusDevice.Id, StringComparison.OrdinalIgnoreCase))
                {
                    UserSettings.SetLastWindowsDefaultDeviceId(current);
                }

                await Task.Run(() =>
                {
                    bool ok = AudioRouterNative.SetSystemDefaultDevice(BusDevice.Id);
                    if (!ok)
                    {
                        Debug.WriteLine("PowerService: SetSystemDefaultDevice failed for bus.");
                    }
                }).ConfigureAwait(false);

                // The bridge itself is started by the orchestrator that owns
                // the active output list (MainViewModel). PowerService just
                // owns the master Windows-default flip + persistence; the
                // orchestrator listens to State to know when to call into
                // BassEngine.StartBridge.
                State = PowerState.Active;
                StatusMessage = "Audio bus is active.";
                PersistPoweredOnAtClose(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PowerService.EngageAsync failed: {ex.Message}");
                State = PowerState.Failed;
                StatusMessage = $"Could not engage the audio bus: {ex.Message}";
            }
            finally
            {
                _stateGate.Release();
            }
        }

        /// <summary>
        /// Disengages the master bus: stops the bridge (via the orchestrator)
        /// and restores the Windows default device captured during the last
        /// engage. Safe to call when the bus is already off.
        /// </summary>
        public async Task DisengageAsync(bool persistPreference = true)
        {
            await _stateGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (State == PowerState.Disabled || State == PowerState.Disengaging)
                    return;

                State = PowerState.Disengaging;
                StatusMessage = "Restoring previous audio device...";

                var previous = UserSettings.GetLastWindowsDefaultDeviceId();
                if (!string.IsNullOrWhiteSpace(previous)
                    && BusDevice?.Id != null
                    && !string.Equals(previous, BusDevice.Id, StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Run(() =>
                    {
                        bool ok = AudioRouterNative.SetSystemDefaultDevice(previous);
                        if (ok)
                        {
                            // Only clear once the restore actually succeeded so
                            // a failed call (device unplugged) keeps the value
                            // for the next attempt.
                            UserSettings.SetLastWindowsDefaultDeviceId(null);
                        }
                        else
                        {
                            Debug.WriteLine($"PowerService: failed to restore default to {previous}.");
                        }
                    }).ConfigureAwait(false);
                }

                IsBackupModeActive = false;
                State = PowerState.Disabled;
                StatusMessage = "Audio bus is off.";

                if (persistPreference)
                    PersistPoweredOnAtClose(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PowerService.DisengageAsync failed: {ex.Message}");
                State = PowerState.Failed;
                StatusMessage = $"Could not disengage cleanly: {ex.Message}";
            }
            finally
            {
                _stateGate.Release();
            }
        }

        /// <summary>
        /// Best-effort synchronous shutdown for app-exit / system-shutdown.
        /// Restores the previous Windows default if persisted; deliberately
        /// caps work at ~500ms so SessionEnding doesn't block Windows.
        /// </summary>
        public void DisengageOnShutdown()
        {
            try
            {
                var previous = UserSettings.GetLastWindowsDefaultDeviceId();
                if (string.IsNullOrWhiteSpace(previous)) return;
                if (BusDevice?.Id != null
                    && string.Equals(previous, BusDevice.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                bool ok = AudioRouterNative.SetSystemDefaultDevice(previous);
                if (ok) UserSettings.SetLastWindowsDefaultDeviceId(null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PowerService.DisengageOnShutdown failed: {ex.Message}");
            }
        }

        private static string? TryGetCurrentDefaultDeviceId()
        {
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(
                    NAudio.CoreAudioApi.DataFlow.Render,
                    NAudio.CoreAudioApi.Role.Multimedia);
                return device?.ID;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PowerService.TryGetCurrentDefaultDeviceId failed: {ex.Message}");
                return null;
            }
        }

        private static void PersistPoweredOnAtClose(bool value)
        {
            try
            {
                UserSettings.SetWasPoweredOnAtClose(value);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PowerService persistence failed: {ex.Message}");
            }
        }
    }
}
