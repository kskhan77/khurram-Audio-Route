namespace KhurramAudioRoute.Core.Latency;

/// <summary>
/// L4 lightweight presets: substring hits on the Windows friendly name seed
/// <see cref="AudioDevice.TargetLatencyOffsetMs"/> before coarse class defaults.
/// Ordered most-specific-first; first match wins. See <c>docs/LATENCY_PLAN.md</c> L4.
/// </summary>
public static class PredictiveLatencyDefaults
{
    private sealed record Rule(string Substring, int OffsetMs);

    /// <summary>
    /// Curated from lab spot-checks + BT/HDMI/TV behaviour — tweak as field data arrives.
    /// </summary>
    private static readonly Rule[] Rules =
    {
        new("wh-1000xm", 28),
        new("wf-1000xm", 26),
        new("airpods max", 34),
        new("airpods pro", 24),
        new("airpods", 22),
        new("bose qc45", 28),
        new("bose qc35", 28),
        new("soundbuds", 24),
        new("galaxy buds", 22),
        new("pixel buds", 22),
        new("echo studio", 38),
        new("echo dot", 42),
        new("soundbar", 14),
        new("apple tv", 16),
        new("fire tv stick", 18),
        new("nvidia shield", 14),
        new("samsung tv", 12),
        new("lg tv", 12),
        new("hisense", 12),
        new("tcl tv", 12),
        new("denon", 10),
        new("yamaha rx", 10),
        new("avr-hdmi", 12),
    };

    /// <summary>Returns true when a known model line matched.</summary>
    public static bool TryMatch(string? friendlyName, out int offsetMs)
    {
        offsetMs = 0;
        if (string.IsNullOrWhiteSpace(friendlyName))
            return false;

        var hay = friendlyName.Trim().ToLowerInvariant();
        foreach (var r in Rules)
        {
            if (hay.Contains(r.Substring, StringComparison.Ordinal))
            {
                offsetMs = r.OffsetMs;
                return true;
            }
        }

        return false;
    }
}
