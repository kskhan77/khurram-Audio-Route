using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Collections.Generic;

namespace KhurramAudioRoute.Core
{
    public enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2, ERole_enum_count = 3 }
    public enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2, EDataFlow_enum_count = 3 }

    // ── System-default routing (LPWSTR, works on all Windows versions) ─────────
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, out IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr pMixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, out long pmftDefaultPeriod, out long pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, long pmftPeriod);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out int pMode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, out IntPtr pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole eRole);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bVisible);
    }

    // ── Windows 11 per-app routing — deviceId is HSTRING (WinRT string handle) ──
    // Using LPWStr here was the bug: Windows passes/expects an HSTRING opaque handle,
    // not a raw wchar_t*. We use IntPtr and create the HSTRING manually via combase.dll.
    [ComImport, Guid("ab413d1a-19c0-4576-9979-7b06703345a0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioPolicyConfigFactory
    {
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr hstrDeviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr hstrDeviceId);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }

    // ── Windows 10 per-app routing — path-based, plain LPWSTR ─────────────────
    [ComImport, Guid("2A59116D-6C4F-45E0-A74F-707E3FCEE415"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioPolicyConfigWin10
    {
        [PreserveSig] int SetPersistedDefaultAudioEndpoint([MarshalAs(UnmanagedType.LPWStr)] string processPath, EDataFlow dataFlow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? endpointId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint([MarshalAs(UnmanagedType.LPWStr)] string processPath, EDataFlow dataFlow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] out string endpointId);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    public class PolicyConfigClient { }

    public static class AudioRouterNative
    {
        // WinRT HSTRING lifecycle — required for the Win11 IAudioPolicyConfigFactory.
        // An HSTRING is an opaque handle, NOT a wchar_t*; passing a raw string pointer fails silently.
        [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int WindowsCreateString(string? sourceString, uint length, out IntPtr hstring);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        // Returns IntPtr.Zero (null HSTRING) when value is null — signals "reset to default"
        private static IntPtr ToHString(string? value)
        {
            if (value == null) return IntPtr.Zero;
            int hr = WindowsCreateString(value, (uint)value.Length, out IntPtr h);
            if (hr != 0) Debug.WriteLine($"WindowsCreateString failed: 0x{hr:X}");
            return h;
        }

        // ── Set system-wide default device ─────────────────────────────────────
        public static bool SetSystemDefaultDevice(string deviceId)
        {
            try
            {
                var config = (new PolicyConfigClient()) as IPolicyConfig;
                if (config == null) { Debug.WriteLine("SetSystemDefault: IPolicyConfig cast failed"); return false; }

                int hr1 = config.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                int hr2 = config.SetDefaultEndpoint(deviceId, ERole.eConsole);
                Debug.WriteLine($"SetSystemDefaultDevice HR1:0x{hr1:X} HR2:0x{hr2:X}");
                return hr1 == 0 || hr2 == 0;
            }
            catch (Exception ex) { Debug.WriteLine($"SetSystemDefaultDevice error: {ex.Message}"); return false; }
        }

        // ── Route a specific process to a device ────────────────────────────────
        // processName: plain name (e.g. "chrome") used for browser multi-PID expansion.
        // deviceId: null resets the process to system default.
        public static bool SetAppDefaultDevice(int pid, string? exePath, string? deviceId, string? processName = null)
        {
            var client = new PolicyConfigClient();
            bool ok = false;

            // ── 1. Win11: PID-based, HSTRING deviceId ─────────────────────────
            try
            {
                var factory = client as IAudioPolicyConfigFactory;
                if (factory == null)
                {
                    Debug.WriteLine("Win11: IAudioPolicyConfigFactory QI failed — falling back to Win10 path");
                }
                else
                {
                    var pids = BuildPidSet(pid, exePath, processName);
                    Debug.WriteLine($"Win11 routing {pids.Count} PID(s) → {deviceId ?? "DEFAULT"}");

                    IntPtr hDevice = ToHString(deviceId);
                    try
                    {
                        foreach (var p in pids)
                        {
                            int hr1 = factory.SetPersistedDefaultAudioEndpoint(p, EDataFlow.eRender, ERole.eMultimedia, hDevice);
                            int hr2 = factory.SetPersistedDefaultAudioEndpoint(p, EDataFlow.eRender, ERole.eConsole, hDevice);
                            int hr3 = factory.SetPersistedDefaultAudioEndpoint(p, EDataFlow.eRender, ERole.eCommunications, hDevice);
                            Debug.WriteLine($"  PID {p}: 0x{hr1:X}  0x{hr2:X}  0x{hr3:X}");
                            if (hr1 == 0 || hr2 == 0) ok = true;
                        }
                    }
                    finally
                    {
                        if (hDevice != IntPtr.Zero) WindowsDeleteString(hDevice);
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"Win11 routing exception: {ex.Message}"); }

            // ── 2. Win10 fallback: path-based, LPWSTR ─────────────────────────
            if (!ok && !string.IsNullOrEmpty(exePath))
            {
                try
                {
                    var cfg10 = client as IAudioPolicyConfigWin10;
                    if (cfg10 == null)
                    {
                        Debug.WriteLine("Win10: IAudioPolicyConfigWin10 QI also failed — no routing path available for this Windows build");
                    }
                    else
                    {
                        Debug.WriteLine($"Win10 path routing: {exePath} → {deviceId ?? "DEFAULT"}");
                        int hr1 = cfg10.SetPersistedDefaultAudioEndpoint(exePath, EDataFlow.eRender, ERole.eMultimedia, deviceId);
                        int hr2 = cfg10.SetPersistedDefaultAudioEndpoint(exePath, EDataFlow.eRender, ERole.eConsole, deviceId);
                        int hr3 = cfg10.SetPersistedDefaultAudioEndpoint(exePath, EDataFlow.eRender, ERole.eCommunications, deviceId);
                        Debug.WriteLine($"  Win10 HRs: 0x{hr1:X}  0x{hr2:X}  0x{hr3:X}");
                        if (hr1 == 0 || hr2 == 0) ok = true;
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"Win10 routing exception: {ex.Message}"); }
            }
            else if (!ok)
            {
                Debug.WriteLine($"Win10 path skipped — exePath is null/empty for PID {pid}. QueryFullProcessImageName may need elevation.");
            }

            if (!ok) Debug.WriteLine($"SetAppDefaultDevice: all paths failed for PID {pid}");
            return ok;
        }

        public static bool ResetAppDefaultDevice(int pid, string? exePath, string? processName = null)
            => SetAppDefaultDevice(pid, exePath, null, processName);

        // Builds the set of PIDs to route — expands Chrome/Edge to all sibling processes
        private static HashSet<uint> BuildPidSet(int pid, string? exePath, string? processName)
        {
            var set = new HashSet<uint> { (uint)pid };

            string name = processName?.ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(exePath))
                name = System.IO.Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
            if (string.IsNullOrEmpty(name))
                try { name = Process.GetProcessById(pid).ProcessName.ToLowerInvariant(); } catch { }

            if (!string.IsNullOrEmpty(name) &&
                (name.Contains("chrome") || name.Contains("msedge") || name.Contains("browser")))
            {
                foreach (var p in Process.GetProcessesByName(name))
                    set.Add((uint)p.Id);
            }

            return set;
        }
    }
}
