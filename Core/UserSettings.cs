using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using KhurramAudioRoute.Core.Spatial;

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
