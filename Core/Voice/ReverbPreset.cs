namespace KhurramAudioRoute.Core.Voice;

/// <summary>Reverb preset for the Voice Studio card. None = bypassed.</summary>
public enum ReverbPreset
{
    None,
    VoiceBooth,
    VocalPlate,
    StudioRoom,
    ConcertHall,
    Cathedral,
}

/// <summary>Bundle of <see cref="VoiceReverb"/> parameters written by a preset.</summary>
public readonly record struct ReverbPresetBundle(
    bool Enabled,
    float RoomSize,
    float Damping,
    float WetMix);

public static class ReverbPresets
{
    /// <summary>Resolve a preset to its bundle.</summary>
    public static ReverbPresetBundle Resolve(ReverbPreset preset) => preset switch
    {
        // Short, dry vocal-booth feel — barely-there air.
        ReverbPreset.VoiceBooth  => new ReverbPresetBundle(true,  0.30f, 0.65f, 0.12f),
        // Sparkly, mid-length plate — sounds like a vintage studio plate.
        ReverbPreset.VocalPlate  => new ReverbPresetBundle(true,  0.70f, 0.20f, 0.22f),
        // Natural medium room — the safe "podcast" choice.
        ReverbPreset.StudioRoom  => new ReverbPresetBundle(true,  0.50f, 0.45f, 0.18f),
        // Long lush hall — adds dramatic body.
        ReverbPreset.ConcertHall => new ReverbPresetBundle(true,  0.85f, 0.30f, 0.28f),
        // Very long, slightly damped — cathedral / cave.
        ReverbPreset.Cathedral   => new ReverbPresetBundle(true,  0.95f, 0.50f, 0.32f),
        // None: bypass.
        _                        => new ReverbPresetBundle(false, 0.50f, 0.40f, 0f),
    };

    /// <summary>Display label for the pill button.</summary>
    public static string DisplayName(ReverbPreset preset) => preset switch
    {
        ReverbPreset.None        => "None",
        ReverbPreset.VoiceBooth  => "Voice booth",
        ReverbPreset.VocalPlate  => "Plate",
        ReverbPreset.StudioRoom  => "Studio room",
        ReverbPreset.ConcertHall => "Hall",
        ReverbPreset.Cathedral   => "Cathedral",
        _                        => preset.ToString(),
    };
}
