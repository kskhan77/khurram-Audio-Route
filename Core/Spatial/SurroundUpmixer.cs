namespace KhurramAudioRoute.Core.Spatial;

/// <summary>
/// Float-domain stereo → 5.1 / 7.1 expansion matching <see cref="MatrixUpmixStage"/>
/// channel ordering (see class doc on <c>MatrixUpmixStage</c>). Used by the master
/// bridge when feeding native multichannel WASAPI at 48 kHz.
/// </summary>
public static class SurroundUpmixer
{
    private static readonly float Mid = (float)Math.Sqrt(0.5);

    /// <param name="stereo">Interleaved L,R × <paramref name="frames"/>.</param>
    /// <param name="dst">Interleaved surround × <paramref name="frames"/>; must be 6 or 8 channels.</param>
    public static void ExpandFrames(ReadOnlySpan<float> stereo, Span<float> dst, int frames, int outChannels)
    {
        if (outChannels != 6 && outChannels != 8)
            throw new ArgumentOutOfRangeException(nameof(outChannels));
        if (frames <= 0) return;
        if (stereo.Length < frames * 2 || dst.Length < frames * outChannels)
            throw new ArgumentException("Span length too small for frame count.");

        for (int f = 0; f < frames; f++)
        {
            float L = stereo[f * 2];
            float R = stereo[f * 2 + 1];

            float fc = (L + R) * Mid;
            float bl = (L - R) * Mid;
            float br = (R - L) * Mid;
            int o = f * outChannels;

            if (outChannels == 6)
            {
                dst[o + 0] = L;
                dst[o + 1] = R;
                dst[o + 2] = fc;
                dst[o + 3] = 0f;
                dst[o + 4] = bl;
                dst[o + 5] = br;
            }
            else
            {
                dst[o + 0] = L;
                dst[o + 1] = R;
                dst[o + 2] = fc;
                dst[o + 3] = 0f;
                dst[o + 4] = bl * Mid;
                dst[o + 5] = br * Mid;
                dst[o + 6] = bl;
                dst[o + 7] = br;
            }
        }
    }
}
