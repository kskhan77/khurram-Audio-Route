namespace KhurramAudioRoute.Core.Voice;

/// <summary>
/// Canned "voice character" presets — bundles EQ preset, pitch, and gate
/// values into a single click. None = honor whatever the sliders / EQ pill say.
/// See <c>docs/MIC_CHAIN_PLAN.md</c>.
/// </summary>
public enum VoiceCharacterPreset
{
    None,
    Robot,
    Girl1,
    Girl2,
    DeepVoice,
    Chipmunk,
}

/// <summary>
/// Snapshot of every Voice Studio control written by a character preset.
/// Returned by <see cref="VoiceCharacterPresets.Resolve"/>.
/// </summary>
public readonly record struct VoiceCharacterBundle(
    VoiceEqPreset Eq,
    float PitchSemitones,
    float GateThresholdDb,
    float GateHoldMs);

public static class VoiceCharacterPresets
{
    /// <summary>
    /// Resolve a character to its bundle. <see cref="VoiceCharacterPreset.None"/>
    /// returns the safe default (EQ=Off, no pitch shift, gate at -40/80).
    /// </summary>
    public static VoiceCharacterBundle Resolve(VoiceCharacterPreset preset) => preset switch
    {
        VoiceCharacterPreset.Robot     => new VoiceCharacterBundle(VoiceEqPreset.Telephone, 0f,    -32f, 60f),
        VoiceCharacterPreset.Girl1     => new VoiceCharacterBundle(VoiceEqPreset.Bright,    +5f,   -40f, 80f),
        VoiceCharacterPreset.Girl2     => new VoiceCharacterBundle(VoiceEqPreset.Warm,      +3f,   -40f, 80f),
        VoiceCharacterPreset.DeepVoice => new VoiceCharacterBundle(VoiceEqPreset.Warm,      -4f,   -40f, 80f),
        VoiceCharacterPreset.Chipmunk  => new VoiceCharacterBundle(VoiceEqPreset.Bright,    +8f,   -40f, 80f),
        _                              => new VoiceCharacterBundle(VoiceEqPreset.Off,       0f,    -40f, 80f),
    };

    /// <summary>Display label shown on the pill button.</summary>
    public static string DisplayName(VoiceCharacterPreset preset) => preset switch
    {
        VoiceCharacterPreset.None      => "None",
        VoiceCharacterPreset.Robot     => "Robot",
        VoiceCharacterPreset.Girl1     => "Girl 1",
        VoiceCharacterPreset.Girl2     => "Girl 2",
        VoiceCharacterPreset.DeepVoice => "Deep",
        VoiceCharacterPreset.Chipmunk  => "Chipmunk",
        _                              => preset.ToString(),
    };
}
