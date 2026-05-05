using System.Diagnostics;
using ManagedBass;
using ManagedBass.DirectX8;
using ManagedBass.Fx;

namespace KhurramAudioRoute.Core;

/// <summary>
/// Canonical playback-side DSP for the VB-CABLE / backup bus.
/// Mirrors <c>docs/AUDIO_BUS_PLAN.md</c>: EQ on the bridge master mixer + spatial upstream,
/// followed by an always-on soft limit stage on the decoded bus so heavy EQ bumps don't clip.
/// </summary>
public static class MasterEngine
{
    public static readonly float[] IsoCenterFrequencies =
        { 31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };

    /// <summary>
    /// BASS DSP priority ordering: larger values run nearer the decoder (earlier).
    /// Peak EQ must precede the post-bus limiter, so EQ uses the higher priority.
    /// </summary>
    public const int PlaybackBusPeakEqFxPriority = 10;

    public const int PlaybackBusLimiterFxPriority = 3;

    /// <summary>DX Param EQ bandwidth (effect-specific units); tuned so neighbour ISO bands don’t collide badly.</summary>
    private const float DxParamEqBandwidth = 30f;

    /// <summary>Attaches multi-band PeakEQ sliders to <paramref name="mixerStream"/>.</summary>
    public static int[] AttachIsoPeakEq(int mixerStream, float[] gains)
        => AttachIsoPeakEq(mixerStream, gains, out _);

    /// <summary>
    /// Attaches ISO graphic EQ. BASS_FX <see cref="EffectType.PeakEQ"/> often fails with error Type on
    /// BASSmix <b>decode</b> streams (VB-CABLE graph); in that case switches to DX8 <see cref="EffectType.DXParamEQ"/>.
    /// </summary>
    public static int[] AttachIsoPeakEq(int mixerStream, float[] gains, out bool usesDxParamEq)
    {
        usesDxParamEq = false;
        int[] handles = new int[IsoCenterFrequencies.Length];

        int probe = Bass.ChannelSetFX(mixerStream, EffectType.PeakEQ, PlaybackBusPeakEqFxPriority);
        if (probe != 0)
            Bass.ChannelRemoveFX(mixerStream, probe);
        else
        {
            usesDxParamEq = true;
            Debug.WriteLine($"MasterEngine: BASS_FX PeakEQ unavailable ({Bass.LastError}) — using DX8 Param EQ");

            for (int i = 0; i < IsoCenterFrequencies.Length; i++)
            {
                handles[i] = Bass.ChannelSetFX(mixerStream, EffectType.DXParamEQ, PlaybackBusPeakEqFxPriority + i + 1);
                if (handles[i] == 0)
                {
                    Debug.WriteLine($"MasterEngine: DX Param EQ band {i} attach failed ({Bass.LastError})");
                    continue;
                }

                var dx = new DXParamEQParameters
                {
                    fCenter = IsoCenterFrequencies[i],
                    fBandwidth = DxParamEqBandwidth,
                    fGain = i < gains.Length ? gains[i] : 0f,
                };
                Bass.FXSetParameters(handles[i], dx);
            }

            int attachedDx = 0;
            for (int k = 0; k < handles.Length; k++) if (handles[k] != 0) attachedDx++;
            Debug.WriteLine($"MasterEngine.AttachIsoPeakEq: mixer={mixerStream}, mode=DX8 ParamEQ, bands attached={attachedDx}/{IsoCenterFrequencies.Length}");
            return handles;
        }

        for (int i = 0; i < IsoCenterFrequencies.Length; i++)
        {
            handles[i] = Bass.ChannelSetFX(mixerStream, EffectType.PeakEQ, PlaybackBusPeakEqFxPriority + i + 1);
            if (handles[i] == 0)
            {
                Debug.WriteLine($"MasterEngine: PeakEQ band {i} attach failed ({Bass.LastError})");
                continue;
            }

            SetIsoPeakEqBand(handles[i], i, i < gains.Length ? gains[i] : 0f);
        }

        int attached = 0;
        for (int i = 0; i < handles.Length; i++) if (handles[i] != 0) attached++;
        Debug.WriteLine($"MasterEngine.AttachIsoPeakEq: mixer={mixerStream}, mode={(usesDxParamEq ? "DX8 ParamEQ" : "BASS_FX PeakEQ")}, bands attached={attached}/{IsoCenterFrequencies.Length}");
        return handles;
    }

    /// <summary>Updates gains for handles returned by <see cref="AttachIsoPeakEq(int,float[],out bool)"/>.</summary>
    public static void ApplyIsoPeakEqGains(int[] handles, float[] gains, bool usesDxParamEq)
    {
        if (handles == null || gains == null) return;

        for (int i = 0; i < IsoCenterFrequencies.Length && i < handles.Length; i++)
        {
            if (handles[i] == 0) continue;
            float g = i < gains.Length ? gains[i] : 0f;

            if (usesDxParamEq)
            {
                var dx = new DXParamEQParameters
                {
                    fCenter = IsoCenterFrequencies[i],
                    fBandwidth = DxParamEqBandwidth,
                    fGain = g,
                };
                Bass.FXSetParameters(handles[i], dx);
            }
            else
                SetIsoPeakEqBand(handles[i], i, g);
        }
    }

    public static bool SetIsoPeakEqBand(int fxHandle, int band, float gain)
    {
        if (fxHandle == 0 || band < 0 || band >= IsoCenterFrequencies.Length)
            return false;

        var eq = new PeakEQParameters
        {
            lBand = band,
            fCenter = IsoCenterFrequencies[band],
            fBandwidth = 2.5f,
            fGain = gain,
            lChannel = FXChannelFlags.All
        };

        bool ok = Bass.FXSetParameters(fxHandle, eq);
        if (!ok)
            Debug.WriteLine($"MasterEngine: PeakEQ band {band} update failed ({Bass.LastError})");
        return ok;
    }

    /// <returns>Compressor FX handle, or <c>0</c> when <paramref name="mixerStream"/> is invalid.</returns>
    public static int AttachBusSoftLimiterFx(int mixerStream)
    {
        if (mixerStream == 0) return 0;

        // DX8 effects are more likely to succeed on BASSmix decode paths than BASS_FX “Compressor”
        // (the latter often returns error Type on mixers).
        int fx = Bass.ChannelSetFX(mixerStream, EffectType.DXCompressor, PlaybackBusLimiterFxPriority);
        if (fx != 0)
        {
            var dx = new DXCompressorParameters
            {
                fGain = 0f,
                fThreshold = -4f,
                fRatio = 12f,
                fAttack = 3f,
                fRelease = 120f,
                fPredelay = 0f,
            };
            Bass.FXSetParameters(fx, dx);
            return fx;
        }

        fx = Bass.ChannelSetFX(mixerStream, EffectType.Compressor, PlaybackBusLimiterFxPriority);
        if (fx != 0)
        {
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

        Debug.WriteLine($"MasterEngine: no bus limiter (DX8 + BASS_FX compressors both failed, last {Bass.LastError})");
        return 0;
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
