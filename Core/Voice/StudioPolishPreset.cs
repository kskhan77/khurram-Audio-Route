namespace KhurramAudioRoute.Core.Voice;

/// <summary>"Studio polish" macro presets — bundles compressor + de-esser settings.</summary>
public enum StudioPolishPreset
{
    None,
    Soft,
    Strong,
    Broadcast,
}

/// <summary>Bundle of compressor + de-esser parameters written by a polish preset.</summary>
public readonly record struct StudioPolishBundle(
    bool CompressorEnabled,
    float CompressorThresholdDb,
    float CompressorRatio,
    float CompressorMakeupDb,
    bool DeEsserEnabled,
    float DeEsserThresholdDb,
    float DeEsserMaxReductionDb);

public static class StudioPolishPresets
{
    /// <summary>Resolve a preset to its bundle.</summary>
    public static StudioPolishBundle Resolve(StudioPolishPreset preset) => preset switch
    {
        // Light polish: gentle 2:1, modest de-essing.
        StudioPolishPreset.Soft      => new StudioPolishBundle(true, -16f, 2.0f, 2f, true, -22f, -4f),
        // "Studio voice" — 3:1, +4 dB makeup, firm de-ess.
        StudioPolishPreset.Strong    => new StudioPolishBundle(true, -18f, 3.0f, 4f, true, -22f, -8f),
        // Broadcast: aggressive 4:1, +6 dB, heavier de-essing — radio/podcast feel.
        StudioPolishPreset.Broadcast => new StudioPolishBundle(true, -22f, 4.0f, 6f, true, -24f, -10f),
        // None: both stages bypassed.
        _                            => new StudioPolishBundle(false, -18f, 3.0f, 4f, false, -22f, -8f),
    };

    /// <summary>Display label for the pill button.</summary>
    public static string DisplayName(StudioPolishPreset preset) => preset switch
    {
        StudioPolishPreset.None      => "None",
        StudioPolishPreset.Soft      => "Soft",
        StudioPolishPreset.Strong    => "Strong",
        StudioPolishPreset.Broadcast => "Broadcast",
        _                            => preset.ToString(),
    };
}
