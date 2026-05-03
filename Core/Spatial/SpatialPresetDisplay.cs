using System;
using System.Globalization;
using System.Windows.Data;

namespace KhurramAudioRoute.Core.Spatial
{
    /// <summary>
    /// Friendly display names for <see cref="SpatialPreset"/> enum values. The
    /// raw enum names (e.g. "HeadphoneStereoPlus") leak through the ComboBox
    /// otherwise; this gives the UI a single place to override them.
    /// </summary>
    public static class SpatialPresetDisplay
    {
        public static string Name(SpatialPreset p) => p switch
        {
            SpatialPreset.Off                  => "Off",
            SpatialPreset.HeadphoneStereoPlus  => "Headphone Stereo+",
            SpatialPreset.HeadphoneStudio      => "Headphone Studio",
            SpatialPreset.HeadphoneCinema      => "Headphone Cinema (7.1 + theater)",
            SpatialPreset.HeadphoneConcertHall => "Headphone Concert Hall (5.1 + hall)",
            SpatialPreset.Speakers_5_1         => "5.1 Speakers (upmix)",
            SpatialPreset.Speakers_7_1         => "7.1 Speakers (upmix)",
            SpatialPreset.GameMode             => "Game Mode (low latency)",
            _ => p.ToString()
        };

        /// <summary>Tight labels for vertical pill selectors (tooltip keeps full Name).</summary>
        public static string ShortName(SpatialPreset p) => p switch
        {
            SpatialPreset.Off                  => "Off",
            SpatialPreset.HeadphoneStereoPlus  => "Stereo+",
            SpatialPreset.HeadphoneStudio      => "Studio",
            SpatialPreset.HeadphoneCinema      => "Cinema 7.1",
            SpatialPreset.HeadphoneConcertHall => "Concert hall",
            SpatialPreset.Speakers_5_1         => "5.1 speakers",
            SpatialPreset.Speakers_7_1         => "7.1 speakers",
            SpatialPreset.GameMode             => "Game",
            _ => p.ToString()
        };
    }

    /// <summary>
    /// XAML one-way converter: <see cref="SpatialPreset"/> -> friendly display
    /// string. Handles ComboBox ItemTemplate and SelectionBoxItem rendering.
    /// </summary>
    public sealed class SpatialPresetDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is SpatialPreset p ? SpatialPresetDisplay.Name(p) : value?.ToString() ?? string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
