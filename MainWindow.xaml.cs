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
using KhurramAudioRoute.Tests;

namespace KhurramAudioRoute;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : FluentWindow
{
    private bool _isExplicitExit;
    private readonly DispatcherTimer _meterTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(220)
    };

    public MainWindow()
    {
        InitializeComponent();
        
        var viewModel = new MainViewModel();
        viewModel.RefreshData();
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;
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

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            Hide();
        }
    }

    public void ExitApplication()
    {
        _isExplicitExit = true;
        Application.Current.Shutdown();
    }

    public void ShowWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _meterTimer.Stop();
        _meterTimer.Tick -= OnMeterTimerTick;
        TrayIcon?.Dispose();
        Loaded -= OnLoaded;
        Closing -= OnClosing;
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

    // The master strips on Applications + Outputs are GLOBAL controls: mute affects
    // every output device, and the volume slider sets every output to the same level.
    // The strip's DataContext is still MasterDevice so the IsMuted/Volume binding shows
    // the default device's state - we just fan the action out to the rest.
    private void OnMasterVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider slider || slider.DataContext is not AudioDevice device)
            return;

        if (!IsLoaded)
            return;

        ApplyVolumeToAllOutputs((float)e.NewValue, device);
    }

    private void OnMasterMuteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AudioDevice device)
            return;

        bool nextMuted = !device.IsMuted;
        ApplyMuteToAllOutputs(nextMuted);
    }

    private void ApplyVolumeToAllOutputs(float level, AudioDevice anchor)
    {
        if (DataContext is not MainViewModel vm) return;
        foreach (var d in vm.Devices)
        {
            if (string.IsNullOrWhiteSpace(d.Id)) continue;
            DeviceManager.SetMasterVolume(d.Id, level);
            // Keep the model in sync so the per-device volume slider on the Outputs
            // cards reflects the master move immediately, without waiting for the
            // 220 ms meter tick to repoll device levels.
            d.Volume = level;
        }
    }

    private void ApplyMuteToAllOutputs(bool muted)
    {
        if (DataContext is not MainViewModel vm) return;
        foreach (var d in vm.Devices)
        {
            if (string.IsNullOrWhiteSpace(d.Id)) continue;
            DeviceManager.SetMasterMute(d.Id, muted);
            d.IsMuted = muted;
        }
    }

    // Mirror sync offset +/- buttons. Tag carries the DeviceSelection so the same
    // handler works for every row in the MIRROR TO list.
    private void OnIncreaseLatency(object sender, RoutedEventArgs e)
        => AdjustTargetLatency(sender, +DeviceSelection.LatencyStepMs);

    private void OnDecreaseLatency(object sender, RoutedEventArgs e)
        => AdjustTargetLatency(sender, -DeviceSelection.LatencyStepMs);

    private static void AdjustTargetLatency(object sender, int deltaMs)
    {
        if (sender is not FrameworkElement element)
            return;

        var selection = (element.Tag as DeviceSelection) ?? element.DataContext as DeviceSelection;
        if (selection == null)
            return;

        int updated = selection.LatencyOffsetMs + deltaMs;
        selection.LatencyOffsetMs = Math.Clamp(updated, DeviceSelection.MinLatencyOffsetMs, DeviceSelection.MaxLatencyOffsetMs);
    }

    // The Outputs-page strip uses the same global handlers as the Applications strip;
    // these wrappers exist only because the XAML Click/ValueChanged attributes name them.
    private void OnOutputsMasterMuteClicked(object sender, RoutedEventArgs e) => OnMasterMuteClicked(sender, e);
    private void OnOutputsMasterVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => OnMasterVolumeChanged(sender, e);

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

    private void OnRunDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            AudioCoreTester.RunTests();
            System.Windows.MessageBox.Show("Core integrity tests completed. Check the debug output for detailed results.", "Diagnostics", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Diagnostic test failed: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    // 10 ISO-octave bands: 31, 62, 125, 250, 500, 1k, 2k, 4k, 8k, 16k Hz.
    private static readonly float[] PresetFlat = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly float[] PresetBass = { 7, 6, 4, 2, 0, -1, -2, -1, 0, 1 };
    private static readonly float[] PresetVoice = { -4, -3, -1, 2, 4, 5, 4, 2, 0, -1 };
    private static readonly float[] PresetBright = { -3, -2, -1, 0, 0, 1, 3, 5, 5, 4 };

    private static void ApplyEqualizerPreset(AudioDevice device, string preset)
    {
        float[] gains = preset switch
        {
            "Bass" => PresetBass,
            "Voice" => PresetVoice,
            "Bright" => PresetBright,
            _ => PresetFlat,
        };

        device.SetEqualizerGains(gains);
    }

    private void OnTrayIconDoubleClick(object sender, RoutedEventArgs e) => ShowWindow();
    private void OnShowAppClicked(object sender, RoutedEventArgs e) => ShowWindow();
    private void OnExitAppClicked(object sender, RoutedEventArgs e) => ExitApplication();
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

/// <summary>
/// Hides a panel until its DataContext is non-null. Used so the master output
/// strip stays collapsed during the brief startup window before the default device is loaded.
/// </summary>
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
