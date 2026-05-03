using System;
using System.Collections.Generic;
using KhurramAudioRoute.Core;

namespace KhurramAudioRoute.Core.SyncCalibration;

/// <summary>
/// Compares persisted L2 fingerprints with a one-shot WASAPI probe whenever the render device list is refreshed.
/// </summary>
public static class L2CalibrationStaleHints
{
    public static void Refresh(IEnumerable<AudioDevice>? devices)
    {
        if (devices == null) return;

        foreach (var d in devices)
        {
            if (d.IsSonicFlowVirtual || string.IsNullOrWhiteSpace(d.Id))
            {
                d.SyncCalibrationStale = false;
                continue;
            }

            var saved = UserSettings.GetL2Calibration(d.Id!);
            if (saved == null || string.IsNullOrWhiteSpace(saved.WaveFormatFingerprint))
            {
                d.SyncCalibrationStale = false;
                continue;
            }

            if (!SyncClickPlayer.TryCaptureFingerprint(d.Id!, out var live))
            {
                d.SyncCalibrationStale = false;
                continue;
            }

            d.SyncCalibrationStale = !string.Equals(
                saved.WaveFormatFingerprint,
                live,
                StringComparison.Ordinal);
        }
    }
}
