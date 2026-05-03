using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using KhurramAudioRoute.Core.Spatial;
using KhurramAudioRoute.Core.SyncCalibration;

namespace KhurramAudioRoute.Core
{
    /// <summary>
    /// Persistent per-user app settings. Backed by a JSON file at
    /// <c>%APPDATA%\SonicFlow\settings.json</c>. Tiny by design — only data
    /// that has to survive a restart belongs here.
    ///
    /// Loaded once on app startup (lazy) and rewritten on every Set call.
    /// All access goes through the static API; an internal lock keeps
    /// concurrent saves consistent.
    /// </summary>
    public static class UserSettings
    {
        private sealed class SettingsModel
        {
            // Map of Windows device endpoint ID -> spatial preset name. Devices not
            // in the map default to SpatialPreset.Off when read.
            public Dictionary<string, string> SpatialPresets { get; set; } = new();

            // Map of Windows device endpoint ID -> 10-band EQ gains.
            public Dictionary<string, float[]> EqualizerGains { get; set; } = new();

            // ── Master bus state (v1) ──────────────────────────────────────────
            // True when the user had the master power button ON the last time
            // the app exited cleanly. Drives auto-engage on the next launch.
            public bool WasPoweredOnAtClose { get; set; }

            // The Windows default playback device that was active right before
            // the master engine took over. Used to restore the user's setup on
            // disengage / app exit / system shutdown. Empty when no override
            // is in flight.
            public string? LastWindowsDefaultDeviceId { get; set; }

            // Master 10-band EQ gains applied at the bus level (one curve for
            // every active output). Defaults to flat (all zeros).
            public float[] MasterEqualizerGains { get; set; } = new float[10];

            // Master spatial preset name applied to the bus stream. Stored as
            // a string so the JSON survives enum reordering.
            public string MasterSpatialPreset { get; set; } = "Off";

            // Map of device-class key (e.g. "BT", "HDMI", "USB", "OnBoard") ->
            // baseline LatencyOffsetMs. Populated lazily by the L1 heuristic
            // and editable per device. See docs/LATENCY_PLAN.md.
            public Dictionary<string, int> LatencyClassDefaults { get; set; } = new();

            // Per-physical-endpoint target offset used by the master bus.
            // Survives device hot-plug; deleted only by the user's reset.
            public Dictionary<string, int> TargetLatencyOffsets { get; set; } = new();

            // Outputs / Applications cards: collapsed vs expanded Advanced section per endpoint id.
            public Dictionary<string, bool> AdvancedExpandedByDeviceId { get; set; } = new();

            public Dictionary<string, L2SyncCalibrationRow> L2SyncCalibrations { get; set; } = new();

            /// <summary>
            /// When true, suppress the modal shown on startup when VB-CABLE is missing.
            /// </summary>
            public bool SuppressVbCableStartupReminder { get; set; }
        }

        private static readonly object _gate = new();
        private static SettingsModel? _cache;

        private static string SettingsPath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SonicFlow");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.json");
            }
        }

        public static SpatialPreset GetSpatialPreset(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return SpatialPreset.Off;
            var s = LoadCached();
            if (s.SpatialPresets.TryGetValue(deviceId, out var name)
                && Enum.TryParse<SpatialPreset>(name, out var preset))
            {
                return preset;
            }
            return SpatialPreset.Off;
        }

        public static void SetSpatialPreset(string deviceId, SpatialPreset preset)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            lock (_gate)
            {
                var s = LoadCachedLocked();
                s.SpatialPresets[deviceId] = preset.ToString();
                SaveLocked(s);
            }
        }

        public static float[]? GetEqualizerGains(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;
            var s = LoadCached();
            return s.EqualizerGains.TryGetValue(deviceId, out var gains) ? gains : null;
        }

        public static void SetEqualizerGains(string deviceId, float[] gains)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || gains == null) return;
            lock (_gate)
            {
                var s = LoadCachedLocked();
                s.EqualizerGains[deviceId] = gains;
                SaveLocked(s);
            }
        }

        // ── Master bus accessors ──────────────────────────────────────────────

        public static bool GetWasPoweredOnAtClose() => LoadCached().WasPoweredOnAtClose;

        public static void SetWasPoweredOnAtClose(bool value)
        {
            lock (_gate)
            {
                var s = LoadCachedLocked();
                if (s.WasPoweredOnAtClose == value) return;
                s.WasPoweredOnAtClose = value;
                SaveLocked(s);
            }
        }

        public static string? GetLastWindowsDefaultDeviceId() => LoadCached().LastWindowsDefaultDeviceId;

        public static void SetLastWindowsDefaultDeviceId(string? deviceId)
        {
            lock (_gate)
            {
                var s = LoadCachedLocked();
                if (string.Equals(s.LastWindowsDefaultDeviceId, deviceId, StringComparison.Ordinal)) return;
                s.LastWindowsDefaultDeviceId = deviceId;
                SaveLocked(s);
            }
        }

        public static float[] GetMasterEqualizerGains()
        {
            var raw = LoadCached().MasterEqualizerGains;
            // Be defensive in case an older settings file had fewer than 10 bands.
            if (raw == null || raw.Length != 10)
            {
                var fixedUp = new float[10];
                if (raw != null)
                    Array.Copy(raw, fixedUp, Math.Min(raw.Length, 10));
                return fixedUp;
            }
            return (float[])raw.Clone();
        }

        public static void SetMasterEqualizerGains(float[] gains)
        {
            if (gains == null || gains.Length != 10) return;
            lock (_gate)
            {
                var s = LoadCachedLocked();
                s.MasterEqualizerGains = (float[])gains.Clone();
                SaveLocked(s);
            }
        }

        public static SpatialPreset GetMasterSpatialPreset()
        {
            var s = LoadCached();
            return Enum.TryParse<SpatialPreset>(s.MasterSpatialPreset, out var preset)
                ? preset
                : SpatialPreset.Off;
        }

        public static void SetMasterSpatialPreset(SpatialPreset preset)
        {
            lock (_gate)
            {
                var s = LoadCachedLocked();
                var name = preset.ToString();
                if (string.Equals(s.MasterSpatialPreset, name, StringComparison.Ordinal)) return;
                s.MasterSpatialPreset = name;
                SaveLocked(s);
            }
        }

        public static IReadOnlyDictionary<string, int> GetLatencyClassDefaults()
            => new Dictionary<string, int>(LoadCached().LatencyClassDefaults);

        public static void SetLatencyClassDefault(string classKey, int latencyMs)
        {
            if (string.IsNullOrWhiteSpace(classKey)) return;
            lock (_gate)
            {
                var s = LoadCachedLocked();
                if (s.LatencyClassDefaults.TryGetValue(classKey, out var existing) && existing == latencyMs)
                    return;
                s.LatencyClassDefaults[classKey] = latencyMs;
                SaveLocked(s);
            }
        }

        public static int? GetTargetLatencyOffset(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;
            return LoadCached().TargetLatencyOffsets.TryGetValue(deviceId, out var ms) ? ms : null;
        }

        public static void SetTargetLatencyOffset(string deviceId, int latencyMs)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            lock (_gate)
            {
                var s = LoadCachedLocked();
                if (s.TargetLatencyOffsets.TryGetValue(deviceId, out var existing) && existing == latencyMs)
                    return;
                s.TargetLatencyOffsets[deviceId] = latencyMs;
                SaveLocked(s);
            }
        }

        public static bool GetAdvancedExpanded(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            var dict = LoadCached().AdvancedExpandedByDeviceId;
            return dict != null && dict.TryGetValue(deviceId, out var expanded) && expanded;
        }

        /// <summary>When <paramref name="expanded"/> is false, the key is removed to keep JSON small.</summary>
        public static void SetAdvancedExpanded(string deviceId, bool expanded)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            lock (_gate)
            {
                var s = LoadCachedLocked();
                s.AdvancedExpandedByDeviceId ??= new Dictionary<string, bool>();
                if (!expanded)
                {
                    if (s.AdvancedExpandedByDeviceId.Remove(deviceId))
                        SaveLocked(s);
                    return;
                }

                if (s.AdvancedExpandedByDeviceId.TryGetValue(deviceId, out var cur) && cur)
                    return;
                s.AdvancedExpandedByDeviceId[deviceId] = true;
                SaveLocked(s);
            }
        }

        public static L2SyncCalibrationRow? GetL2Calibration(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;
            var dict = LoadCached().L2SyncCalibrations;
            return dict != null && dict.TryGetValue(deviceId, out var row) ? row : null;
        }

        public static void SetL2Calibration(string deviceId, string waveFormatFingerprint)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            lock (_gate)
            {
                var s = LoadCachedLocked();
                s.L2SyncCalibrations ??= new Dictionary<string, L2SyncCalibrationRow>();
                s.L2SyncCalibrations[deviceId] = new L2SyncCalibrationRow
                {
                    CompletedUtcTicks = DateTime.UtcNow.Ticks,
                    WaveFormatFingerprint = waveFormatFingerprint ?? string.Empty
                };
                SaveLocked(s);
            }
        }

        public static bool GetSuppressVbCableStartupReminder() =>
            LoadCached().SuppressVbCableStartupReminder;

        public static void SetSuppressVbCableStartupReminder(bool value)
        {
            lock (_gate)
            {
                var s = LoadCachedLocked();
                if (s.SuppressVbCableStartupReminder == value) return;
                s.SuppressVbCableStartupReminder = value;
                SaveLocked(s);
            }
        }

        private static SettingsModel LoadCached()
        {
            lock (_gate) return LoadCachedLocked();
        }

        private static SettingsModel LoadCachedLocked()
        {
            if (_cache != null) return _cache;

            try
            {
                var path = SettingsPath;
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    _cache = JsonSerializer.Deserialize<SettingsModel>(json) ?? new SettingsModel();
                    _cache.AdvancedExpandedByDeviceId ??= new Dictionary<string, bool>();
                    _cache.L2SyncCalibrations ??= new Dictionary<string, L2SyncCalibrationRow>();
                    return _cache;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UserSettings load failed: {ex.Message}");
            }
            _cache = new SettingsModel();
            return _cache;
        }

        // Atomic write: serialize to a sibling temp file then move into place,
        // so a crash mid-write can't corrupt the existing settings.
        private static void SaveLocked(SettingsModel s)
        {
            try
            {
                var path = SettingsPath;
                var tmp  = path + ".tmp";
                var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UserSettings save failed: {ex.Message}");
            }
        }
    }
}
