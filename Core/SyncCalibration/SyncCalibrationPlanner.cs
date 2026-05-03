using NAudio.CoreAudioApi;

namespace KhurramAudioRoute.Core.SyncCalibration;

/// <summary>L2 perceptual-sync reference / targets — <c>docs/LATENCY_PLAN.md</c>.</summary>
public static class SyncCalibrationPlanner
{
    public const int OffsetStepMs = 20;
    public const int MinBridgeOffsetMs = 0;
    public const int MaxBridgeOffsetMs = 120;

    public static bool TryPrepare(
        IReadOnlyList<AudioDevice> devices,
        bool isBackupModeActive,
        out string? errorMessage,
        out string? referenceDeviceId,
        out List<AudioDevice> calibrationTargets)
    {
        calibrationTargets = new List<AudioDevice>();
        referenceDeviceId = null;
        errorMessage = null;

        var physicalActive = devices
            .Where(d => d is { IsActiveOutput: true, IsSonicFlowVirtual: false } && !string.IsNullOrWhiteSpace(d.Id))
            .ToList();

        if (physicalActive.Count < 2)
        {
            errorMessage =
                "You need at least two hardware playback devices marked ACTIVE to run the sync wizard. "
                + "Tap ACTIVE on headphones and speakers, then reopen this wizard.";
            return false;
        }

        if (isBackupModeActive)
        {
            string? multimediaRef = TryGetMultimediaRenderDefaultDeviceId();
            referenceDeviceId = multimediaRef;
            if (string.IsNullOrWhiteSpace(referenceDeviceId))
            {
                errorMessage =
                    "Could not read Windows default multimedia playback. Plug in a speaker or headphone output and retry.";
                return false;
            }

            string refId = referenceDeviceId;
            calibrationTargets = physicalActive
                .Where(d => !string.Equals(d.Id, refId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (calibrationTargets.Count == 0)
            {
                errorMessage =
                    "Backup mode listens for the dry Windows default playback device versus your other ACTIVE outputs. "
                    + "Activate a second speaker or headset so something can be calibrated.";
                return false;
            }
        }
        else
        {
            var refCandidate = physicalActive
                .OrderBy(d => d.TargetLatencyOffsetMs)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .First();

            referenceDeviceId = refCandidate.Id;
            string rid = refCandidate.Id ?? string.Empty;
            calibrationTargets = physicalActive
                .Where(d => !string.Equals(d.Id, rid, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (calibrationTargets.Count == 0)
            {
                errorMessage = "Could not resolve a calibration target besides the timing reference.";
                return false;
            }
        }

        return true;
    }

    internal static string? TryGetMultimediaRenderDefaultDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return dev.ID;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Multimedia render default: {ex.Message}");
            return null;
        }
    }
}
