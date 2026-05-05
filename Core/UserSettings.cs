using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhurramAudioRoute.Core.Spatial;
using KhurramAudioRoute.Core.SyncCalibration;
using KhurramAudioRoute.Core.SyncCalibration.L3;
using KhurramAudioRoute.Core.Voice;

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

            // Master stereo-width multiplier (mid/side scale). 1.0 = no effect;
            // < 1 narrows toward mono; > 1 widens. Applied as the FIRST stage
            // of every non-Off spatial preset pipeline.
            public float MasterStereoWidth { get; set; } = 1.0f;

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

            // Per-physical-endpoint L3 auto-sync result (offset + timestamp).
            // Drives the "Auto-synced N days ago" tag on each ACTIVE card.
            public Dictionary<string, L3AutoSyncRow> L3AutoSyncCalibrations { get; set; } = new();

            /// <summary>
            /// When true, suppress the modal shown on startup when VB-CABLE is missing.
            /// </summary>
            public bool SuppressVbCableStartupReminder { get; set; }

            /// <summary>
            /// When true, multichannel outputs at 48 kHz (6/8 ch) use matrix stereo→surround
            /// expansion instead of BASS generic mixer upmix. Experimental — see PLAN / BassEngine.
            /// </summary>
            public bool MatrixSurroundBridgeUpmix { get; set; }

            /// <summary>
            /// Matrix bridge channel packing: Auto | Internal | WindowsHdmi7_1 — see <see cref="MatrixBridgeChannelReorder"/>.
            /// </summary>
            public string MatrixBridgeChannelOrder { get; set; } = MatrixBridgeChannelReorder.OrderAuto;

            // ── Mic chain (CABLE-B voice studio) ─────────────────────────────
            // Persisted across restarts so the Voice Studio card on the
            // Microphones page can re-engage the same setup automatically.
            public string? MicChainMicId { get; set; }
            public string? MicChainRenderId { get; set; }
            public bool MicChainEnabled { get; set; }
            public float MicChainGateThresholdDb { get; set; } = -40f;
            public float MicChainGateHoldMs { get; set; } = 80f;
            public string MicChainVoicePreset { get; set; } = "None";
            public float MicChainPitchSemitones { get; set; } = 0f;
            public string MicChainStudioPolish { get; set; } = "None";
            public string MicChainReverb { get; set; } = "None";
            public string MicChainNoiseReduction { get; set; } = "Off";

            /// <summary>
            /// Set of device endpoint IDs that the user has marked ACTIVE (in
            /// the master bridge fan-out). Persists across app restarts so the
            /// previously-selected group is restored on launch. Empty set =
            /// no auto-restored selection (defaults applied at runtime).
            /// </summary>
            public List<string> ActiveBridgeTargetIds { get; set; } = new();

            /// <summary>
            /// True once the user has explicitly toggled an ACTIVE chip at
            /// least once. Used to distinguish "fresh install" (no signal)
            /// from "explicitly chose nothing" (empty list is the answer).
            /// </summary>
            public bool ActiveBridgeTargetsExplicit { get; set; }
        }

        private static readonly object _gate = new();
        private static SettingsModel? _cache;
        private static string? _settingsPath;

        private static string SettingsPath
        {
            get
            {
                if (_settingsPath != null) return _settingsPath;

                foreach (var dir in CandidateSettingsDirs())
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        var path = Path.Combine(dir, "settings.json");
                        EnsureSettingsPathWritable(dir, path);
                        _settingsPath = path;
                        return _settingsPath;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"UserSettings path skipped ({dir}): {ex.Message}");
                    }
                }

                var fallbackDir = Path.Combine(Path.GetTempPath(), "SonicFlow");
                Directory.CreateDirectory(fallbackDir);
                _settingsPath = Path.Combine(fallbackDir, "settings.json");
                return _settingsPath;
            }
        }

        private static IEnumerable<string> CandidateSettingsDirs()
        {
            // Prefer LocalApplicationData first — roaming (%AppData%) often syncs via OneDrive and can
            // flip to read-only or deny atomic writes mid-session (UnauthorizedAccessException).
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local))
                yield return Path.Combine(local, "SonicFlow");

            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(roaming))
                yield return Path.Combine(roaming, "SonicFlow");
        }

        private static void EnsureSettingsPathWritable(string dir, string path)
        {
            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
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

        public static float GetMasterStereoWidth()
        {
            float v = LoadCached().MasterStereoWidth;
            if (v <= 0f) return 1.0f; // defensive: legacy file w/o the field deserialises as 0
            return Math.Clamp(v, 0.5f, 1.6f);
        }

        public static void SetMasterStereoWidth(float width)
        {
            float clamped = Math.Clamp(width, 0.5f, 1.6f);
            lock (_gate)
            {
                var s = LoadCachedLocked();
                if (Math.Abs(s.MasterStereoWidth - clamped) < 0.0005f) return;
                s.MasterStereoWidth = clamped;
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

        /// <summary>Clears user overrides so <see cref="DeviceClassInfo"/> baselines apply again.</summary>
        public static void ClearLatencyClassDefaults()
        {
            lock (_gate)
            {
                var s = LoadCachedLocked();
                if (s.LatencyClassDefaults.Count == 0) return;
                s.LatencyClassDefaults.Clear();
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

        public static L3AutoSyncRow? GetL3AutoSync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;
            var dict = LoadCached().L3AutoSyncCalibrations;
            return dict != null && dict.TryGetValue(deviceId, out var row) ? row : null;
        }

        public static void SetL3AutoSync(string deviceId, int offsetMs, float snrDb, string? waveFormatFingerprint = null)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            lock (_gate)
            {
                var s = LoadCachedLocked();
                s.L3AutoSyncCalibrations ??= new Dictionary<string, L3AutoSyncRow>();
                s.L3AutoSyncCalibrations[deviceId] = new L3AutoSyncRow
                {
                    CompletedUtcTicks = DateTime.UtcNow.Ticks,
                    OffsetMs = offsetMs,
                    SnrDb = snrDb,
                    WaveFormatFingerprint = waveFormatFingerprint ?? string.Empty,
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

        public static bool GetMatrixSurroundBridgeUpmix() => LoadCached().MatrixSurroundBridgeUpmix;

        public static void SetMatrixSurroundBridgeUpmix(bool value)
        {
            lock (_gate)
            {
                var s = LoadCachedLocked();
                if (s.MatrixSurroundBridgeUpmix == value) return;
                s.MatrixSurroundBridgeUpmix = value;
                SaveLocked(s);
            }
        }

        public static string GetMatrixBridgeChannelOrder()
        {
            var v = LoadCached().MatrixBridgeChannelOrder?.Trim();
            if (string.IsNullOrEmpty(v)) return MatrixBridgeChannelReorder.OrderAuto;
            if (v != MatrixBridgeChannelReorder.OrderAuto
                && v != MatrixBridgeChannelReorder.OrderInternal
                && v != MatrixBridgeChannelReorder.OrderWindowsHdmi7_1)
                return MatrixBridgeChannelReorder.OrderAuto;
            return v;
        }

        public static void SetMatrixBridgeChannelOrder(string value)
        {
            var normalised = string.IsNullOrWhiteSpace(value) ? MatrixBridgeChannelReorder.OrderAuto : value.Trim();
            if (normalised != MatrixBridgeChannelReorder.OrderAuto
                && normalised != MatrixBridgeChannelReorder.OrderInternal
                && normalised != MatrixBridgeChannelReorder.OrderWindowsHdmi7_1)
                normalised = MatrixBridgeChannelReorder.OrderAuto;

            lock (_gate)
            {
                var s = LoadCachedLocked();
                if (string.Equals(s.MatrixBridgeChannelOrder, normalised, StringComparison.Ordinal)) return;
                s.MatrixBridgeChannelOrder = normalised;
                SaveLocked(s);
            }
        }

        // ── Mic chain accessors ───────────────────────────────────────────────

        public static string? GetMicChainMicId() => LoadCached().MicChainMicId;

        public static void SetMicChainMicId(string? deviceId)
        {
            lock (_gate)
            {
                var s = LoadCachedLocked();
                if (string.Equals(s.MicChainMicId, deviceId, StringComparison.Ordinal)) return;
                s.MicChainMicId = deviceId;
                SaveLocked(s);
            }
        }

        public static string? GetMicChainRenderId() => LoadCached().MicChainRenderId;

        public static void SetMicChainRenderId(string? deviceId)
        {
            lock (_gate)
            {
                var s = LoadCachedLocked();
                if (string.Equals(s.MicChainRenderId, deviceId, StringComparison.Ordinal)) return;
                s.MicChainRenderId = deviceId;
                SaveLocked(s);
            }
        }

        public static bool GetMicChainEnabled() => LoadCached().MicChainEnabled;

        public static void SetMicChainEnabled(bool value)
        {
            lock (_gate)
            {
                var s = LoadCachedLocked();
                if (s.MicChainEnabled == value) return;
                s.MicChainEnabled = value;
                SaveLocked(s);
            }
        }

        public static float GetMicChainGateThresholdDb()
        {
            float v = LoadCached().MicChainGateThresholdDb;
            return Math.Clamp(v, -80f, 0f);
        }

        public static void SetMicChainGateThresholdDb(float db)
        {
            float clamped = Math.Clamp(db, -80f, 0f);
            lock (_gate)
            {
                var s = LoadCachedLocked();
                if (Math.Abs(s.MicChainGateThresholdDb - clamped) < 0.05f) return;
                s.MicChainGateThresholdDb = clamped;
                SaveLocked(s);
            }
        }

        public static float GetMicChainGateHoldMs()
        {
            float v = LoadCached().MicChainGateHoldMs;
            return Math.Clamp(v, 0f, 1000f);
        }

        public static void SetMicChainGateHoldMs(float ms)
        {
            float clamped = Math.Clamp(ms, 0f, 1000f);
            lock (_gate)
            {
                var s = LoadCachedLocked();
                if (Math.Abs(s.MicChainGateHoldMs - clamped) < 0.5f) return;
                s.MicChainGateHoldMs = clamped;
                SaveLocked(s);
            }
        }

        public static VoicePreset? GetMicChainVoicePreset()
        {
            var s = LoadCached();
            if (string.IsNullOrWhiteSpace(s.MicChainVoicePreset)) return null;
            return Enum.TryParse<VoicePreset>(s.MicChainVoicePreset, out var p) ? p : (VoicePreset?)null;
        }

        public static void SetMicChainVoicePreset(VoicePreset? preset)
        {
            lock (_gate)
            {
                var s = LoadCachedLocked();
                string name = preset?.ToString() ?? string.Empty;
                if (string.Equals(s.MicChainVoicePreset, name, StringComparison.Ordinal)) return;
                s.MicChainVoicePreset = name;
                SaveLocked(s);
            }
        }

        public static float GetMicChainPitchSemitones()
        {
            float v = LoadCached().MicChainPitchSemitones;
            return Math.Clamp(v, PitchShifter.MinSemitones, PitchShifter.MaxSemitones);
        }

        public static void SetMicChainPitchSemitones(float semitones)
        {
            float clamped = Math.Clamp(semitones, PitchShifter.MinSemitones, PitchShifter.MaxSemitones);
            lock (_gate)
            {
                var s = LoadCachedLocked();
                if (Math.Abs(s.MicChainPitchSemitones - clamped) < 0.01f) return;
                s.MicChainPitchSemitones = clamped;
                SaveLocked(s);
            }
        }

        public static StudioPolishPreset GetMicChainStudioPolish()
        {
            var s = LoadCached();
            return Enum.TryParse<StudioPolishPreset>(s.MicChainStudioPolish, out var p) ? p : StudioPolishPreset.None;
        }

        public static void SetMicChainStudioPolish(StudioPolishPreset preset)
        {
            lock (_gate)
            {
                var s = LoadCachedLocked();
                var name = preset.ToString();
                if (string.Equals(s.MicChainStudioPolish, name, StringComparison.Ordinal)) return;
                s.MicChainStudioPolish = name;
                SaveLocked(s);
            }
        }

        public static ReverbPreset GetMicChainReverb()
        {
            var s = LoadCached();
            return Enum.TryParse<ReverbPreset>(s.MicChainReverb, out var p) ? p : ReverbPreset.None;
        }

        public static void SetMicChainReverb(ReverbPreset preset)
        {
            lock (_gate)
            {
                var s = LoadCachedLocked();
                var name = preset.ToString();
                if (string.Equals(s.MicChainReverb, name, StringComparison.Ordinal)) return;
                s.MicChainReverb = name;
                SaveLocked(s);
            }
        }

        public static IReadOnlySet<string> GetActiveBridgeTargetIds()
        {
            var s = LoadCached();
            return new HashSet<string>(s.ActiveBridgeTargetIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public static bool GetActiveBridgeTargetsExplicit() => LoadCached().ActiveBridgeTargetsExplicit;

        public static void SetActiveBridgeTargetIds(IEnumerable<string> ids)
        {
            var list = ids?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                       ?? new List<string>();
            lock (_gate)
            {
                var s = LoadCachedLocked();
                s.ActiveBridgeTargetIds = list;
                s.ActiveBridgeTargetsExplicit = true;
                SaveLocked(s);
            }
        }

        public static NoiseReductionPreset GetMicChainNoiseReduction()
        {
            var s = LoadCached();
            return Enum.TryParse<NoiseReductionPreset>(s.MicChainNoiseReduction, out var p) ? p : NoiseReductionPreset.Off;
        }

        public static void SetMicChainNoiseReduction(NoiseReductionPreset preset)
        {
            lock (_gate)
            {
                var s = LoadCachedLocked();
                var name = preset.ToString();
                if (string.Equals(s.MicChainNoiseReduction, name, StringComparison.Ordinal)) return;
                s.MicChainNoiseReduction = name;
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
                    _cache.L3AutoSyncCalibrations ??= new Dictionary<string, L3AutoSyncRow>();
                    _cache.MatrixBridgeChannelOrder ??= MatrixBridgeChannelReorder.OrderAuto;
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
        private static void SaveLocked(SettingsModel s) => SaveLockedCore(s, resetPathOnFailure: true);

        private static void SaveLockedCore(SettingsModel s, bool resetPathOnFailure)
        {
            try
            {
                var path = SettingsPath;
                var tmp  = path + ".tmp";
                var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (resetPathOnFailure
                                       && (ex is UnauthorizedAccessException or IOException))
            {
                System.Diagnostics.Debug.WriteLine($"UserSettings save failed (retry new path): {ex.Message}");
                _settingsPath = null;
                SaveLockedCore(s, resetPathOnFailure: false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UserSettings save failed: {ex.Message}");
            }
        }
    }
}
