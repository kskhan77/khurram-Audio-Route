using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KhurramAudioRoute.Core.SyncCalibration.L3;

/// <summary>
/// Orchestrates per-target mic-based auto-sync:
///   for each ACTIVE physical target,
///     play the log-sweep,
///     capture the mic,
///     cross-correlate to recover time-of-flight,
///   then normalise so the earliest-arriving target sits at offset 0
///   and the others get positive offsets to match it.
/// See <c>docs/LATENCY_PLAN.md</c> phase L3.
/// </summary>
public static class AutoSyncRunner
{
    public const int MinAcceptedOffsetMs = 0;
    public const int MaxAcceptedOffsetMs = 500;
    public const float MinSnrDb = 9f;
    public const int CaptureLeadMs = 150;
    public const int CaptureTailMs = 500;

    public sealed class Result
    {
        public List<TargetMeasurement> Targets { get; } = new();
        public bool Success => Targets.Count > 0 && Targets.All(t => t.Accepted);
        public string? FailureReason { get; set; }
    }

    public sealed class TargetMeasurement
    {
        public required string DeviceId { get; init; }
        public required string DeviceName { get; init; }
        public int RawLagMs { get; set; }
        public int NormalisedOffsetMs { get; set; }
        public float SnrDb { get; set; }
        public bool Accepted { get; set; }
        public string? RejectReason { get; set; }
    }

    public readonly record struct Target(string DeviceId, string DeviceName);

    /// <summary>
    /// Run the auto-sync pass. Each target is measured sequentially. Returns
    /// the per-target raw lag, the normalised offset, and whether the result
    /// passes the SNR / range sanity gates. Caller is responsible for writing
    /// accepted offsets back to <c>AudioDevice.TargetLatencyOffsetMs</c> and
    /// for persisting via <c>UserSettings</c>.
    /// </summary>
    public static async Task<Result> RunAsync(
        IReadOnlyList<Target> targets,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var result = new Result();
        if (targets is null || targets.Count == 0)
        {
            result.FailureReason = "No ACTIVE hardware outputs to calibrate.";
            return result;
        }

        if (!MicCapture.TryGetDefaultMicId(out _, out string? micName))
        {
            result.FailureReason = "Could not open the system microphone for measurement.";
            return result;
        }
        progress?.Report($"Using mic: {micName ?? "(default)"}");

        foreach (var t in targets)
        {
            ct.ThrowIfCancellationRequested();
            var m = new TargetMeasurement { DeviceId = t.DeviceId, DeviceName = t.DeviceName };
            result.Targets.Add(m);

            var others = new List<string>(targets.Count - 1);
            foreach (var o in targets)
                if (!string.Equals(o.DeviceId, t.DeviceId, StringComparison.OrdinalIgnoreCase))
                    others.Add(o.DeviceId);

            try
            {
                progress?.Report($"Measuring {t.DeviceName}...");
                using var mute = new OutputMuteScope(others);
                await MeasureOneAsync(t.DeviceId, m, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                m.Accepted = false;
                m.RejectReason = ex.Message;
                Debug.WriteLine($"L3 measure {t.DeviceName} threw: {ex}");
            }
        }

        Normalise(result);
        return result;
    }

    private static async Task MeasureOneAsync(string deviceId, TargetMeasurement m, CancellationToken ct)
    {
        using var mic = new MicCapture();
        mic.Start(targetCapacityHintSamples: 48000 * 2);
        int rate = mic.SampleRate;

        await Task.Delay(CaptureLeadMs, ct).ConfigureAwait(false);

        float[] sweep = LogSweepGenerator.Generate(rate);
        await Task.Run(() => SweepPlayer.PlayBlocking(deviceId, sweep, rate), ct).ConfigureAwait(false);
        await Task.Delay(CaptureTailMs, ct).ConfigureAwait(false);

        mic.Stop();
        float[] captured = mic.Snapshot();

        if (captured.Length < sweep.Length)
        {
            m.Accepted = false;
            m.RejectReason = "Mic capture too short — measurement aborted.";
            return;
        }

        int maxLagSamples = rate / 2; // 500 ms ceiling
        var xc = CrossCorrelator.Correlate(captured, sweep, maxLagSamples);
        m.SnrDb = xc.SnrDb;

        if (xc.LagSamples < 0)
        {
            m.Accepted = false;
            m.RejectReason = "No usable correlation peak.";
            return;
        }

        int lagMs = (int)Math.Round(xc.LagSamples * 1000.0 / rate);
        m.RawLagMs = lagMs;

        if (lagMs < MinAcceptedOffsetMs || lagMs > MaxAcceptedOffsetMs)
        {
            m.Accepted = false;
            m.RejectReason = $"Lag {lagMs} ms outside [{MinAcceptedOffsetMs}, {MaxAcceptedOffsetMs}] ms.";
            return;
        }
        if (xc.SnrDb < MinSnrDb)
        {
            m.Accepted = false;
            m.RejectReason = $"SNR {xc.SnrDb:F1} dB below {MinSnrDb:F0} dB gate — couldn't hear it cleanly.";
            return;
        }

        m.Accepted = true;
    }

    private static void Normalise(Result result)
    {
        var accepted = result.Targets.Where(t => t.Accepted).ToList();
        if (accepted.Count == 0)
        {
            result.FailureReason ??= "All targets rejected by SNR / range gates.";
            return;
        }

        int min = accepted.Min(t => t.RawLagMs);
        foreach (var t in accepted)
            t.NormalisedOffsetMs = Math.Clamp(t.RawLagMs - min, MinAcceptedOffsetMs, MaxAcceptedOffsetMs);
    }
}
