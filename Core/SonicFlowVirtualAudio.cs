using System.Collections.Generic;
using System.Linq;

namespace KhurramAudioRoute.Core
{
    public static class SonicFlowVirtualAudio
    {
        public const string ProductRenderName = "SonicFlow Virtual Speaker";
        public const string ProductCaptureName = "SonicFlow Virtual Output";
        public const string HardwareId = "Root\\SonicFlowVirtualAudio";

        private static readonly string[] RenderNameMarkers =
        {
            "sonicflow virtual",
            "sonicflow speaker",
            "sonicflow audio",
            "sysvad"
        };

        public static bool IsVirtualRenderEndpoint(string? endpointId, string? friendlyName)
        {
            var haystack = $"{friendlyName} {endpointId}".ToLowerInvariant();
            return RenderNameMarkers.Any(haystack.Contains);
        }

        public static AudioDevice? FindVirtualRenderDevice(IEnumerable<AudioDevice> devices)
            => devices.FirstOrDefault(d => d.IsSonicFlowVirtual || IsVirtualRenderEndpoint(d.Id, d.Name));

        public static IReadOnlyList<AudioDevice> SortVirtualFirst(IEnumerable<AudioDevice> devices)
            => devices
                .OrderBy(d => d.IsSonicFlowVirtual ? 0 : 1)
                .ThenBy(d => d.Name)
                .ToList();

        public static string BuildStatus(AudioDevice? virtualDevice)
        {
            if (virtualDevice == null)
                return "Virtual driver is not installed yet. Build/install the SonicFlow virtual audio driver first.";

            return virtualDevice.IsDefault
                ? "SonicFlow virtual device is the Windows default output."
                : "SonicFlow virtual device is installed but not set as default.";
        }

        public static bool TrySetAsDefault(AudioDevice? virtualDevice, out string status)
        {
            if (virtualDevice?.Id == null)
            {
                status = BuildStatus(null);
                return false;
            }

            bool ok = AudioRouterNative.SetSystemDefaultDevice(virtualDevice.Id);
            status = ok
                ? "SonicFlow virtual device was set as the Windows default output."
                : $"Could not set {virtualDevice.Name ?? ProductRenderName} as the Windows default output.";

            return ok;
        }
    }
}
