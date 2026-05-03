namespace KhurramAudioRoute.Core.Spatial;

/// <summary>
/// Multichannel ordering for <see cref="SurroundUpmixer"/> output on the BASS bridge.
/// Windows shared-mode 7.1 interleaving is typically FL, FR, FC, LFE, BL, BR, SL, SR
/// (mask bit order); our matrix places SL/SR before BL/BR — swap for many HDMI receivers.
/// </summary>
public static class MatrixBridgeChannelReorder
{
    public const string OrderAuto = "Auto";
    public const string OrderInternal = "Internal";
    public const string OrderWindowsHdmi7_1 = "WindowsHdmi7_1";

    /// <summary>
    /// Sonic matrix / <see cref="SurroundUpmixer"/> layout per frame:
    /// FL, FR, FC, LFE, SL (-3 dB), SR (-3 dB), BL, BR.
    /// </summary>
    public static void SonicMatrixToWindowsHdmi7Dot1(Span<float> interleaved8, int frames)
    {
        if (frames <= 0) return;

        for (int f = 0; f < frames; f++)
        {
            int b = f * 8;
            float fl = interleaved8[b];
            float fr = interleaved8[b + 1];
            float fc = interleaved8[b + 2];
            float lfe = interleaved8[b + 3];
            float sl = interleaved8[b + 4];
            float sr = interleaved8[b + 5];
            float bl = interleaved8[b + 6];
            float br = interleaved8[b + 7];

            interleaved8[b] = fl;
            interleaved8[b + 1] = fr;
            interleaved8[b + 2] = fc;
            interleaved8[b + 3] = lfe;
            interleaved8[b + 4] = bl;
            interleaved8[b + 5] = br;
            interleaved8[b + 6] = sl;
            interleaved8[b + 7] = sr;
        }
    }

    /// <param name="mode">Persisted Tools key: Auto, Internal, or WindowsHdmi7_1.</param>
    public static void Apply(string mode, Span<float> interleaved, int frames, int channels)
    {
        if (channels != 8 || frames <= 0) return;

        bool reorder = mode switch
        {
            OrderWindowsHdmi7_1 => true,
            OrderInternal => false,
            OrderAuto => true,
            _ => true
        };

        if (reorder)
            SonicMatrixToWindowsHdmi7Dot1(interleaved, frames);
    }
}
