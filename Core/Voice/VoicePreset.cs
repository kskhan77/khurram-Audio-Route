namespace KhurramAudioRoute.Core.Voice;

/// <summary>
/// Unified voice preset picker for the Voice Studio card. Replaces the
/// previous split between "Voice EQ" (EQ-only) and "Voice character" (full
/// macros) — they overlapped visually because every character set both EQ
/// and pitch. One row, no overlap, every pill is fully self-describing.
///
/// <list type="bullet">
///   <item><see cref="None"/> — passthrough; doubles as a reset for the row.</item>
///   <item><see cref="Bright"/>/<see cref="Warm"/>/<see cref="Radio"/>/<see cref="Telephone"/> — EQ flavours, no pitch shift.</item>
///   <item><see cref="Robot"/>/<see cref="Girl1"/>/<see cref="Girl2"/>/<see cref="Deep"/>/<see cref="Chipmunk"/> — full character macros (EQ + pitch + gate).</item>
/// </list>
///
/// See <c>docs/MIC_CHAIN_PLAN.md</c>.
/// </summary>
public enum VoicePreset
{
    None,
    Bright,
    Warm,
    Radio,
    Telephone,
    Robot,
    Girl1,
    Girl2,
    Deep,
    Chipmunk,
}

/// <summary>
/// Snapshot of every Voice Studio control written by a voice preset.
/// Returned by <see cref="VoicePresets.Resolve"/>.
/// </summary>
public readonly record struct VoicePresetBundle(
    VoiceEqPreset Eq,
    float PitchSemitones,
    float GateThresholdDb,
    float GateHoldMs);

public static class VoicePresets
{
    /// <summary>
    /// Resolve a preset to its full bundle. <see cref="VoicePreset.None"/>
    /// returns the safe default (EQ=Off, no pitch shift, gate at -40/80) — so
    /// picking "None" actually resets everything the row controls.
    /// </summary>
    public static VoicePresetBundle Resolve(VoicePreset preset) => preset switch
    {
        VoicePreset.Bright    => new VoicePresetBundle(VoiceEqPreset.Bright,    0f, -40f, 80f),
        VoicePreset.Warm      => new VoicePresetBundle(VoiceEqPreset.Warm,      0f, -40f, 80f),
        VoicePreset.Radio     => new VoicePresetBundle(VoiceEqPreset.Radio,     0f, -40f, 80f),
        VoicePreset.Telephone => new VoicePresetBundle(VoiceEqPreset.Telephone, 0f, -40f, 80f),
        VoicePreset.Robot     => new VoicePresetBundle(VoiceEqPreset.Telephone, 0f, -32f, 60f),
        VoicePreset.Girl1     => new VoicePresetBundle(VoiceEqPreset.Bright,   +5f, -40f, 80f),
        VoicePreset.Girl2     => new VoicePresetBundle(VoiceEqPreset.Warm,     +3f, -40f, 80f),
        VoicePreset.Deep      => new VoicePresetBundle(VoiceEqPreset.Warm,     -4f, -40f, 80f),
        VoicePreset.Chipmunk  => new VoicePresetBundle(VoiceEqPreset.Bright,   +8f, -40f, 80f),
        _                     => new VoicePresetBundle(VoiceEqPreset.Off,       0f, -40f, 80f),
    };

    /// <summary>Display label shown on the pill button.</summary>
    public static string DisplayName(VoicePreset preset) => preset switch
    {
        VoicePreset.None      => "None",
        VoicePreset.Bright    => "Bright",
        VoicePreset.Warm      => "Warm",
        VoicePreset.Radio     => "Radio",
        VoicePreset.Telephone => "Telephone",
        VoicePreset.Robot     => "Robot",
        VoicePreset.Girl1     => "Girl 1",
        VoicePreset.Girl2     => "Girl 2",
        VoicePreset.Deep      => "Deep",
        VoicePreset.Chipmunk  => "Chipmunk",
        _                     => preset.ToString(),
    };
}
