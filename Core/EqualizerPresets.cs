using System;
using System.Collections.Generic;

namespace KhurramAudioRoute.Core
{
    /// <summary>
    /// Library of named 10-band ISO-octave EQ presets shared by the per-device
    /// EQ chip strip and the master engine. Centralised here so the two
    /// surfaces can never drift apart.
    ///
    /// Bands (in order): 31, 62, 125, 250, 500, 1k, 2k, 4k, 8k, 16k Hz.
    /// All gain values are dB clamped to the slider range (-12 .. +12).
    /// </summary>
    public static class EqualizerPresets
    {
        public const string Flat      = "Flat";
        public const string Bass      = "Bass";
        public const string Voice     = "Voice";
        public const string Bright    = "Bright";
        public const string Club      = "Club";
        public const string Live      = "Live";
        public const string Pop       = "Pop";
        public const string Rock      = "Rock";
        public const string Classical = "Classical";
        public const string Techno    = "Techno";
        public const string Soft      = "Soft";

        private static readonly Dictionary<string, float[]> Presets = new(StringComparer.OrdinalIgnoreCase)
        {
            [Flat]      = new float[]  { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            [Bass]      = new float[]  { 7, 6, 4, 2, 0, -1, -2, -1, 0, 1 },
            [Voice]     = new float[]  { -4, -3, -1, 2, 4, 5, 4, 2, 0, -1 },
            [Bright]    = new float[]  { -3, -2, -1, 0, 0, 1, 3, 5, 5, 4 },
            [Club]      = new float[]  { 4, 5, 3, 0, 0, 0, 2, 3, 4, 0 },
            [Live]      = new float[]  { -2, 0, 2, 3, 3, 3, 2, 1, 1, 1 },
            [Pop]       = new float[]  { -1, 2, 3, 3, 2, -1, -2, -2, -1, -1 },
            [Rock]      = new float[]  { 5, 4, 3, 1, -1, -1, 1, 3, 4, 5 },
            [Classical] = new float[]  { 4, 4, 3, 2, -1, -1, 0, 2, 4, 4 },
            [Techno]    = new float[]  { 6, 5, 0, -2, -2, 0, 5, 6, 6, 5 },
            [Soft]      = new float[]  { 2, 1, 0, -1, -1, 0, 1, 2, 3, 4 },
        };

        /// <summary>
        /// Returns a defensive clone of the named preset's 10 bands. Unknown
        /// keys (or null/empty) return a fresh flat (all-zero) array, never
        /// null, so callers can apply the result without further checks.
        /// </summary>
        public static float[] Get(string? key)
        {
            string lookup = string.IsNullOrWhiteSpace(key) ? Flat : key.Trim();
            if (!Presets.TryGetValue(lookup, out var gains))
                gains = Presets[Flat];

            return (float[])gains.Clone();
        }

        /// <summary>
        /// Canonical key for a preset name (e.g. trims, fixes casing). Returns
        /// <c>"Flat"</c> for anything unknown so the UI selection stays valid.
        /// </summary>
        public static string Canonical(string? key)
        {
            string lookup = string.IsNullOrWhiteSpace(key) ? Flat : key.Trim();
            return Presets.ContainsKey(lookup) ? lookup : Flat;
        }
    }
}
