namespace KhurramAudioRoute.Core.SyncCalibration.L3;

/// <summary>Serialized L3 auto-sync result per render endpoint (<c>settings.json</c>).</summary>
public sealed class L3AutoSyncRow
{
    public long CompletedUtcTicks { get; set; }
    public int OffsetMs { get; set; }
    public float SnrDb { get; set; }

    /// <summary>WASAPI mix fingerprint at save time (rate × channels × bits). Drives drift detection.</summary>
    public string WaveFormatFingerprint { get; set; } = "";
}
