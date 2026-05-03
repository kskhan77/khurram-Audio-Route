namespace KhurramAudioRoute.Core.SyncCalibration;

/// <summary>Serialized L2 calibration metadata per render endpoint (<c>settings.json</c>).</summary>
public sealed class L2SyncCalibrationRow
{
    public long CompletedUtcTicks { get; set; }

    /// <summary>Lightweight WASAPI fingerprint (sample rate × channels × bits).</summary>
    public string WaveFormatFingerprint { get; set; } = "";
}
