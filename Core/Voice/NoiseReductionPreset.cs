namespace KhurramAudioRoute.Core.Voice;

/// <summary>Noise reduction preset for the Voice Studio card.</summary>
public enum NoiseReductionPreset
{
    Off,
    Light,
    Medium,
    Strong,
}

/// <summary>Bundle of <see cref="SpectralDenoise"/> parameters written by a preset.</summary>
public readonly record struct NoiseReductionBundle(
    bool Enabled,
    float ReductionDb,
    float Overestimate);

public static class NoiseReductionPresets
{
    /// <summary>
    /// Resolve a preset to its bundle. Reduction in dB controls how much the
    /// noise floor is attenuated; overestimate ≥1 widens what counts as noise
    /// (more aggressive but more risk of musical-noise artifacts).
    /// </summary>
    public static NoiseReductionBundle Resolve(NoiseReductionPreset preset) => preset switch
    {
        // Light: subtle clean-up of constant fan / room hum.
        NoiseReductionPreset.Light  => new NoiseReductionBundle(true,  6f,  1.05f),
        // Medium: typical Teams call setup — fan + AC + traffic.
        NoiseReductionPreset.Medium => new NoiseReductionBundle(true, 12f,  1.20f),
        // Strong: noisy environments. May add slight musical-noise on consonants.
        NoiseReductionPreset.Strong => new NoiseReductionBundle(true, 20f,  1.40f),
        _                           => new NoiseReductionBundle(false, 12f, 1.20f),
    };

    /// <summary>Display label for the pill button.</summary>
    public static string DisplayName(NoiseReductionPreset preset) => preset switch
    {
        NoiseReductionPreset.Off    => "Off",
        NoiseReductionPreset.Light  => "Light",
        NoiseReductionPreset.Medium => "Medium",
        NoiseReductionPreset.Strong => "Strong",
        _                           => preset.ToString(),
    };
}
