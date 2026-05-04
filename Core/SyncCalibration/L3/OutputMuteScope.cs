using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace KhurramAudioRoute.Core.SyncCalibration.L3;

/// <summary>
/// Temporarily mutes a set of render endpoints (typically every ACTIVE
/// hardware output except the device under test) so the mic only hears
/// the chirp from the DUT. Original Mute state is restored on Dispose,
/// even if the caller throws.
/// </summary>
public sealed class OutputMuteScope : IDisposable
{
    private readonly List<(MMDevice Device, bool WasMuted)> _restored = new();
    private bool _disposed;

    public OutputMuteScope(IEnumerable<string> deviceIdsToMute)
    {
        if (deviceIdsToMute is null) return;

        var enumerator = new MMDeviceEnumerator();
        try
        {
            foreach (var id in deviceIdsToMute)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                MMDevice? mm = null;
                try
                {
                    mm = enumerator.GetDevice(id);
                    bool wasMuted = mm.AudioEndpointVolume.Mute;
                    if (!wasMuted)
                        mm.AudioEndpointVolume.Mute = true;
                    _restored.Add((mm, wasMuted));
                    mm = null; // ownership transferred to _restored
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OutputMuteScope: skip {id}: {ex.Message}");
                    mm?.Dispose();
                }
            }
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (mm, wasMuted) in _restored)
        {
            try
            {
                if (mm.AudioEndpointVolume.Mute != wasMuted)
                    mm.AudioEndpointVolume.Mute = wasMuted;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OutputMuteScope restore failed: {ex.Message}");
            }
            finally
            {
                try { mm.Dispose(); } catch { }
            }
        }
        _restored.Clear();
    }
}
