using KhurramAudioRoute.Core;
using NAudio.CoreAudioApi;

namespace KhurramAudioRoute.Core.Latency;

/// <summary>
/// Chooses the initial Sync slider ms for a render endpoint when nothing is persisted
/// for that endpoint id: L4 predictive name match → user-edited class map in
/// <see cref="UserSettings.LatencyClassDefaults"/> → L1 class baseline.
/// </summary>
public static class LatencySeedResolver
{
    private const int MinMs = 0;
    private const int MaxMs = 120;

    public static int Resolve(MMDevice endpoint, DeviceClass deviceClass, bool isVirtual)
    {
        if (isVirtual)
            return 0;

        var persisted = UserSettings.GetTargetLatencyOffset(endpoint.ID);
        if (persisted.HasValue)
            return Clamp(persisted.Value);

        try
        {
            var name = endpoint.FriendlyName;
            if (PredictiveLatencyDefaults.TryMatch(name, out var predictive))
                return Clamp(predictive);
        }
        catch { /* MMDevice property can throw when unplug races */ }

        var key = DeviceClassInfo.ShortLabel(deviceClass);
        if (!string.IsNullOrEmpty(key))
        {
            var map = UserSettings.GetLatencyClassDefaults();
            if (map.TryGetValue(key, out var classOverride))
                return Clamp(classOverride);
        }

        return Clamp(DeviceClassInfo.DefaultOffsetMs(deviceClass));
    }

    private static int Clamp(int ms) => Math.Clamp(ms, MinMs, MaxMs);
}
