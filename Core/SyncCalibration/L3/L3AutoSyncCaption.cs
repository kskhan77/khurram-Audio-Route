using System;
using System.Collections.Generic;
using KhurramAudioRoute.Core;

namespace KhurramAudioRoute.Core.SyncCalibration.L3;

/// <summary>
/// Reads persisted L3 auto-sync rows and writes a "Auto-synced N days ago"
/// caption onto each device, plus a drift flag when the saved WASAPI
/// fingerprint disagrees with the live one. Mirrors
/// <c>L2CalibrationStaleHints</c>.
/// </summary>
public static class L3AutoSyncCaption
{
    public static void Refresh(IEnumerable<AudioDevice>? devices)
    {
        if (devices is null) return;

        foreach (var d in devices)
        {
            if (d.IsSonicFlowVirtual || string.IsNullOrWhiteSpace(d.Id))
            {
                d.AutoSyncCaption = string.Empty;
                d.AutoSyncDrift = false;
                continue;
            }

            var row = UserSettings.GetL3AutoSync(d.Id!);
            d.AutoSyncCaption = row is null ? string.Empty : Format(row);

            if (row is null || string.IsNullOrWhiteSpace(row.WaveFormatFingerprint))
            {
                d.AutoSyncDrift = false;
                continue;
            }

            // Reuse L2's WASAPI fingerprint probe — same shape, same source of truth.
            if (!SyncCalibration.SyncClickPlayer.TryCaptureFingerprint(d.Id!, out var live))
            {
                d.AutoSyncDrift = false;
                continue;
            }

            d.AutoSyncDrift = !string.Equals(row.WaveFormatFingerprint, live, StringComparison.Ordinal);
        }
    }

    public static string Format(L3AutoSyncRow row)
    {
        if (row.CompletedUtcTicks <= 0) return string.Empty;
        var elapsed = DateTime.UtcNow - new DateTime(row.CompletedUtcTicks, DateTimeKind.Utc);
        return $"Auto-synced {RelativeAge(elapsed)}";
    }

    private static string RelativeAge(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 60) return "just now";
        if (elapsed.TotalMinutes < 60)
        {
            int m = (int)elapsed.TotalMinutes;
            return m == 1 ? "1 minute ago" : $"{m} minutes ago";
        }
        if (elapsed.TotalHours < 24)
        {
            int h = (int)elapsed.TotalHours;
            return h == 1 ? "1 hour ago" : $"{h} hours ago";
        }
        int days = (int)elapsed.TotalDays;
        return days == 1 ? "1 day ago" : $"{days} days ago";
    }
}
