using System.Diagnostics;
using ManagedBass;
using ManagedBass.Fx;

namespace KhurramAudioRoute.Core;

/// <summary>
/// Canonical playback-side DSP for the VB-CABLE / backup bus.
/// Mirrors <c>docs/AUDIO_BUS_PLAN.md</c>: EQ on the bridge master mixer + spatial upstream,
/// followed by an always-on soft limit stage on the decoded bus so heavy EQ bumps don't clip.
/// </summary>
public static class MasterEngine
{
    /// <summary>
    /// BASS DSP priority ordering: larger values run nearer the decoder (earlier).
    /// Peak EQ must precede the post-bus limiter, so EQ uses the higher priority.
    /// </summary>
    public const int PlaybackBusPeakEqFxPriority = 10;

    public const int PlaybackBusLimiterFxPriority = 3;

    /// <summary>Attaches multi-band PeakEQ sliders to <paramref name="mixerStream"/>.</summary>
    public static int[] AttachIsoPeakEq(int mixerStream, float[] gains)
    {
        float[] centerFreqs = { 31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };
        int[] handles = new int[centerFreqs.Length];

        for (int i = 0; i < centerFreqs.Length; i++)
        {
            handles[i] = Bass.ChannelSetFX(mixerStream, EffectType.PeakEQ, PlaybackBusPeakEqFxPriority);
            Bass.FXSetParameters(handles[i], new PeakEQParameters
            {
                lBand = i,
                fCenter = centerFreqs[i],
                fBandwidth = 2.5f,
                fGain = i < gains.Length ? gains[i] : 0f
            });
        }

        return handles;
    }

    /// <returns>Compressor FX handle, or <c>0</c> when <paramref name="mixerStream"/> is invalid.</returns>
    public static int AttachBusSoftLimiterFx(int mixerStream)
    {
        if (mixerStream == 0) return 0;

        int fx = Bass.ChannelSetFX(mixerStream, EffectType.Compressor, PlaybackBusLimiterFxPriority);
        if (fx == 0)
        {
            Debug.WriteLine($"MasterEngine: compressor FX failed ({Bass.LastError})");
            return 0;
        }

        // Gentle ceiling catching post-EQ overs; complements per-preset Spatial soft limiters when spatial is off.
        var p = new CompressorParameters
        {
            fGain = 0f,
            fThreshold = -4f,
            fRatio = 12f,
            fAttack = 3f,
            fRelease = 120f,
            lChannel = FXChannelFlags.All
        };
        Bass.FXSetParameters(fx, p);
        return fx;
    }

    public static void RemoveFx(int mixerStream, ref int fxHandle)
    {
        if (mixerStream == 0 || fxHandle == 0)
            return;
        try { Bass.ChannelRemoveFX(mixerStream, fxHandle); }
        catch (Exception ex) { Debug.WriteLine($"MasterEngine.RemoveFx: {ex.Message}"); }
        fxHandle = 0;
    }
}
