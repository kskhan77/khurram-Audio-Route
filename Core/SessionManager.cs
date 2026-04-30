using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KhurramAudioRoute.Core
{
    public partial class DeviceSelection : ObservableObject
    {
        public const int MaxLatencyOffsetMs = 500;
        public const int LatencyStepMs = 10;

        public AudioDevice Device { get; set; } = null!;

        [ObservableProperty]
        private bool _isSelected;

        // Per-target playback delay in milliseconds. Used to align mirror sources that
        // run on different transports (e.g. wired vs Bluetooth) so their output stays
        // in phase. Positive values delay this target relative to the source.
        [ObservableProperty]
        private int _latencyOffsetMs;
    }

    public partial class AppAudioSession : ObservableObject
    {
        public int ProcessId { get; set; }
        public string? ProcessName { get; set; }
        public string? ExePath { get; set; }

        [ObservableProperty] private float _volume;
        [ObservableProperty] private float _peakValue;
        [ObservableProperty] private bool _isPlaying;
        [ObservableProperty] private bool _isMuted;
        [ObservableProperty] private bool _isDuplicating;
        [ObservableProperty] private AudioDevice? _selectedTargetDevice;
        [ObservableProperty] private ObservableCollection<DeviceSelection> _targetDevices = new();

        partial void OnVolumeChanged(float value) => SessionManager.SetVolume(ProcessId, value);
        partial void OnIsMutedChanged(bool value) => SessionManager.SetMute(ProcessId, value);
    }

    public static class SessionManager
    {
        // QueryFullProcessImageName succeeds on sandboxed/protected processes that deny
        // access to Process.MainModule — Chrome, Edge, and UWP apps all fall into this category.
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(
            IntPtr hProcess, int dwFlags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // PROCESS_QUERY_LIMITED_INFORMATION — doesn't require admin, works on protected processes
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public static string? GetExePath(int pid)
        {
            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = 1024;
                return QueryFullProcessImageName(handle, 0, sb, ref size)
                    ? sb.ToString(0, size)
                    : null;
            }
            finally { CloseHandle(handle); }
        }

        public static void SetVolume(int pid, float volume)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    try
                    {
                        var sm = device.AudioSessionManager;
                        if (sm == null) { device.Dispose(); continue; }
                        for (int i = 0; i < sm.Sessions.Count; i++)
                        {
                            using var s = sm.Sessions[i];
                            if (s.GetProcessID == (uint)pid)
                            {
                                s.SimpleAudioVolume.Volume = volume;
                                if (volume > 0) s.SimpleAudioVolume.Mute = false;
                            }
                        }
                    }
                    catch { }
                    device.Dispose();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"SetVolume PID {pid}: {ex.Message}"); }
        }

        public static void SetMute(int pid, bool mute)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    try
                    {
                        var sm = device.AudioSessionManager;
                        if (sm == null) { device.Dispose(); continue; }
                        for (int i = 0; i < sm.Sessions.Count; i++)
                        {
                            using var s = sm.Sessions[i];
                            if (s.GetProcessID == (uint)pid)
                                s.SimpleAudioVolume.Mute = mute;
                        }
                    }
                    catch { }
                    device.Dispose();
                }
            }
            catch { }
        }

        public static List<AppAudioSession> GetActiveSessions()
        {
            var map = new Dictionary<string, AppAudioSession>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    try
                    {
                        var sm = device.AudioSessionManager;
                        if (sm == null) { device.Dispose(); continue; }

                        for (int i = 0; i < sm.Sessions.Count; i++)
                        {
                            var session = sm.Sessions[i];
                            try
                            {
                                int pid = (int)session.GetProcessID;
                                if (pid == 0) { session.Dispose(); continue; }

                                string key = pid.ToString();
                                float peak = 0;
                                try { peak = session.AudioMeterInformation.MasterPeakValue; } catch { }

                                if (map.TryGetValue(key, out var existing))
                                {
                                    if (peak > existing.PeakValue)
                                    {
                                        existing.PeakValue = peak;
                                        existing.IsPlaying = peak > 0.001f;
                                    }
                                    session.Dispose();
                                    continue;
                                }

                                string processName = "Unknown";

                                // QueryFullProcessImageName works on sandboxed Chrome/Edge child
                                // processes that block access to Process.MainModule
                                string? exePath = GetExePath(pid);

                                try
                                {
                                    var proc = Process.GetProcessById(pid);
                                    processName = proc.ProcessName;
                                    // Only fall back to MainModule if QueryFullProcessImageName failed
                                    if (exePath == null)
                                        try { exePath = proc.MainModule?.FileName; } catch { }
                                }
                                catch
                                {
                                    processName = !string.IsNullOrEmpty(session.DisplayName)
                                        ? session.DisplayName
                                        : $"Process ({pid})";
                                }

                                Debug.WriteLine($"Session PID {pid} ({processName}) exePath={exePath ?? "null"}");

                                map[key] = new AppAudioSession
                                {
                                    ProcessId = pid,
                                    ProcessName = processName,
                                    ExePath = exePath,
                                    Volume = session.SimpleAudioVolume.Volume,
                                    IsMuted = session.SimpleAudioVolume.Mute,
                                    PeakValue = peak,
                                    IsPlaying = peak > 0.001f
                                };
                            }
                            catch { }
                            finally { session.Dispose(); }
                        }
                    }
                    catch { }
                    finally { device.Dispose(); }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"GetActiveSessions error: {ex.Message}"); }
            return new List<AppAudioSession>(map.Values);
        }

        public static void UpdateSessionLevels(IEnumerable<AppAudioSession> sessions)
        {
            var sessionMap = sessions.ToDictionary(s => s.ProcessId);

            foreach (var session in sessionMap.Values)
            {
                session.PeakValue = 0f;
                session.IsPlaying = false;
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    try
                    {
                        var sm = device.AudioSessionManager;
                        if (sm == null) { device.Dispose(); continue; }

                        for (int i = 0; i < sm.Sessions.Count; i++)
                        {
                            using var audioSession = sm.Sessions[i];
                            int pid = (int)audioSession.GetProcessID;
                            if (!sessionMap.TryGetValue(pid, out var model))
                                continue;

                            float peak = 0f;
                            try { peak = audioSession.AudioMeterInformation.MasterPeakValue; } catch { }

                            model.PeakValue = Math.Max(model.PeakValue, peak);
                            model.IsPlaying = model.PeakValue > 0.001f;

                            try { model.Volume = audioSession.SimpleAudioVolume.Volume; } catch { }
                            try { model.IsMuted = audioSession.SimpleAudioVolume.Mute; } catch { }
                        }
                    }
                    catch { }
                    finally { device.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UpdateSessionLevels error: {ex.Message}");
            }
        }
    }
}
