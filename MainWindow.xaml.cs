using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using KhurramAudioRoute.Core;
using KhurramAudioRoute.ViewModels;

namespace KhurramAudioRoute;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly DispatcherTimer _meterTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(220)
    };

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        _meterTimer.Tick += OnMeterTimerTick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Height = Math.Max(680, workArea.Height * 0.60);
        Width = Math.Min(1350, workArea.Width * 0.82);
        _meterTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _meterTimer.Stop();
        _meterTimer.Tick -= OnMeterTimerTick;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }

    private void OnMeterTimerTick(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.RefreshMeters();
    }

    private void OnOutputDeviceVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider slider || slider.DataContext is not AudioDevice device || string.IsNullOrWhiteSpace(device.Id))
            return;

        if (!IsLoaded)
            return;

        DeviceManager.SetMasterVolume(device.Id, (float)e.NewValue);
    }

    // ToggleSwitch in the Outputs Advanced panel: forwards the click to the
    // ToggleDeviceDuplicate command on the MainViewModel. The switch's IsChecked
    // is OneWay-bound to AudioDevice.IsDuplicating so the VM stays the source of truth.
    private void OnDuplicateToggleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AudioDevice device)
            return;
        if (DataContext is not MainViewModel viewModel)
            return;

        if (viewModel.ToggleDeviceDuplicateCommand.CanExecute(device))
            viewModel.ToggleDeviceDuplicateCommand.Execute(device);
    }

    // Mute toggle next to the device volume slider. Flips the endpoint mute state via
    // WASAPI; the next meter tick re-syncs AudioDevice.IsMuted from the device, so we
    // intentionally don't write IsMuted ourselves to avoid fighting the polled state.
    private void OnDeviceMuteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AudioDevice device || string.IsNullOrWhiteSpace(device.Id))
            return;

        DeviceManager.SetMasterMute(device.Id, !device.IsMuted);
        device.IsMuted = !device.IsMuted;
    }

    private void OnApplyEqPreset(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AudioDevice device)
            return;

        var preset = element.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(preset))
            return;

        ApplyEqualizerPreset(device, preset);
    }

    private void OnResetEq(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AudioDevice device)
            return;

        ApplyEqualizerPreset(device, "Flat");
    }

    private static void ApplyEqualizerPreset(AudioDevice device, string preset)
    {
        switch (preset)
        {
            case "Bass":
                device.EqLow = 6f;
                device.EqLowMid = 3f;
                device.EqMid = 0f;
                device.EqHighMid = -2f;
                device.EqHigh = -1f;
                break;
            case "Voice":
                device.EqLow = -3f;
                device.EqLowMid = 1f;
                device.EqMid = 4f;
                device.EqHighMid = 3f;
                device.EqHigh = 1f;
                break;
            case "Bright":
                device.EqLow = -2f;
                device.EqLowMid = 0f;
                device.EqMid = 2f;
                device.EqHighMid = 5f;
                device.EqHigh = 4f;
                break;
            default:
                device.EqLow = 0f;
                device.EqLowMid = 0f;
                device.EqMid = 0f;
                device.EqHighMid = 0f;
                device.EqHigh = 0f;
                break;
        }
    }
}

/// <summary>
/// Inverts a boolean value. Useful for binding IsMuted to a "Power/Active" toggle.
/// </summary>
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return false;
    }
}

/// <summary>
/// True → Collapsed, False → Visible. Used to hide controls when a flag is set
/// (e.g. hide the "Set Default" button when the device is already default).
/// </summary>
public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts a 0.0-1.0 value to a height based on a multiplier (parameter).
/// </summary>
public class PeakToHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        float val = 0;
        if (value is float f) val = f;
        else if (value is double d) val = (float)d;

        double maxHeight = 150;
        if (parameter != null && double.TryParse(parameter.ToString(), out double p))
            maxHeight = p;

        return val * maxHeight;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Generates staggered meter-bar heights from a single peak value to create a simple spectrum effect.
/// </summary>
public class SpectrumBarHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        float peak = 0f;
        if (value is float f) peak = f;
        else if (value is double d) peak = (float)d;

        var parts = parameter?.ToString()?.Split(',') ?? Array.Empty<string>();
        double multiplier = 1.0;
        double baseline = 5.0;
        double maxHeight = 28.0;

        if (parts.Length > 0)
            double.TryParse(parts[0], out multiplier);
        if (parts.Length > 1)
            double.TryParse(parts[1], out baseline);
        if (parts.Length > 2)
            double.TryParse(parts[2], out maxHeight);

        var height = baseline + (peak * multiplier * maxHeight);
        return Math.Max(4.0, Math.Min(maxHeight, height));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Produces a brighter accent when a device or app is actively playing.
/// </summary>
public class ActivityBrushConverter : IValueConverter
{
    public Brush ActiveBrush { get; set; } = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#74D1FF"));
    public Brush InactiveBrush { get; set; } = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B454D"));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool active && active ? ActiveBrush : InactiveBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Shows a section only when the bound enum matches the requested section name.
/// </summary>
public class SectionVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return Visibility.Collapsed;

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
