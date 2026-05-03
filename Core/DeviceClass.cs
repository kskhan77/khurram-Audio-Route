using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;

namespace KhurramAudioRoute.Core
{
    /// <summary>
    /// Coarse-grain bucket for a Windows render endpoint. Used to seed
    /// sensible default latency offsets without forcing the user to dial in
    /// each new headphone / soundbar / TV by hand. See
    /// <c>docs/LATENCY_PLAN.md</c> for the L1 design.
    /// </summary>
    public enum DeviceClass
    {
        Unknown,
        OnBoard,    // built-in laptop / desktop speakers + 3.5mm jack
        Usb,        // USB DAC / headset
        Hdmi,       // HDMI / DisplayPort / TV
        Bluetooth,  // BT A2DP — can drift, biggest baseline latency
        Network     // AirPlay / Cast / DLNA endpoint
    }

    /// <summary>
    /// Human-friendly text + default-offset table used by both the model and
    /// the UI chip strip. Heuristics intentionally err on the side of
    /// detection — wrong class is fine because the user can override the
    /// offset; missing class would just leave latency at zero.
    /// </summary>
    public static class DeviceClassInfo
    {
        public const int DefaultOnBoardOffsetMs   = 0;
        public const int DefaultUsbOffsetMs       = 0;
        public const int DefaultHdmiOffsetMs      = 10;
        public const int DefaultBluetoothOffsetMs = 30;
        public const int DefaultNetworkOffsetMs   = 35;

        public static int DefaultOffsetMs(DeviceClass cls) => cls switch
        {
            DeviceClass.OnBoard   => DefaultOnBoardOffsetMs,
            DeviceClass.Usb       => DefaultUsbOffsetMs,
            DeviceClass.Hdmi      => DefaultHdmiOffsetMs,
            DeviceClass.Bluetooth => DefaultBluetoothOffsetMs,
            DeviceClass.Network   => DefaultNetworkOffsetMs,
            _                     => 0
        };

        public static string ShortLabel(DeviceClass cls) => cls switch
        {
            DeviceClass.OnBoard   => "On-board",
            DeviceClass.Usb       => "USB",
            DeviceClass.Hdmi      => "HDMI",
            DeviceClass.Bluetooth => "BT",
            DeviceClass.Network   => "Network",
            _                     => string.Empty
        };
    }

    /// <summary>
    /// Best-effort classifier for an <see cref="MMDevice"/>. Walks two
    /// signals: the IPropertyStore icon path Windows assigns based on the
    /// audio class GUID, and the device name. Either match is enough.
    /// </summary>
    public static class DeviceClassResolver
    {
        // Substrings that show up in Windows' icon path (sndvol uses these
        // PNG resource names) or in the friendly name. Lowercased before
        // comparison; first match wins in priority order below.
        private static readonly Dictionary<DeviceClass, string[]> Markers = new()
        {
            [DeviceClass.Bluetooth] = new[] { "bluetooth", "bt-", " bt ", "airpods", "wh-1000", "buds", "qc35", "qc45", "wireless head", "a2dp", "sbc", "aac codec" },
            [DeviceClass.Hdmi]      = new[] { "hdmi", "displayport", "dp ", "tv", "monitor audio", "amd hd audio", "nvidia high def", "intel display audio" },
            [DeviceClass.Usb]       = new[] { "usb", "schiit", "topping", "ifi", "dragonfly", "sound blaster", "scarlett", "fiio", "motu", "presonus", "audio interface", "fifine", "hyperx", "dac" },
            [DeviceClass.Network]   = new[] { "airplay", "cast", "chromecast", "sonos", "dlna", "spotify connect" },
            [DeviceClass.OnBoard]   = new[] { "realtek", "intel smart sound", "conexant", "high definition audio device", "speakers (high definition", "internal speakers", "built-in", "laptop speakers", "headphone (high definition", "stereo realtek" },
        };

        public static DeviceClass Detect(MMDevice? device)
        {
            if (device == null) return DeviceClass.Unknown;

            string name = SafeName(device).ToLowerInvariant();
            string icon = SafeIcon(device).ToLowerInvariant();
            string haystack = name + " | " + icon;

            // Priority order — Bluetooth wins if both BT and headphone-ish
            // markers are present (BT headphones often advertise as Realtek
            // BT-Audio in their friendly name and would otherwise classify
            // as on-board).
            foreach (var key in new[]
            {
                DeviceClass.Bluetooth,
                DeviceClass.Hdmi,
                DeviceClass.Network,
                DeviceClass.Usb,
                DeviceClass.OnBoard,
            })
            {
                if (!Markers.TryGetValue(key, out var marks)) continue;
                foreach (var m in marks)
                {
                    if (haystack.Contains(m, StringComparison.OrdinalIgnoreCase))
                        return key;
                }
            }

            return DeviceClass.Unknown;
        }

        private static string SafeName(MMDevice device)
        {
            try { return device.FriendlyName ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string SafeIcon(MMDevice device)
        {
            try { return device.IconPath ?? string.Empty; }
            catch { return string.Empty; }
        }
    }
}
