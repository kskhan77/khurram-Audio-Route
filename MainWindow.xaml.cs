using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using KhurramAudioRoute.Core;
using KhurramAudioRoute.Core.Spatial;
using KhurramAudioRoute.ViewModels;
using KhurramAudioRoute.Tests;

namespace KhurramAudioRoute;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : FluentWindow
{
    private bool _isExplicitExit;
    private bool _vbCableReminderShown;
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

        // Restore the user's Windows default device when the OS is signing
        // them out / shutting down, even if the app was minimised to tray.
        // PowerService.DisengageOnShutdown is best-effort and capped to the
        // SessionEnding budget so we don't block Windows.
        Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;

        // Keep the tray menu header in sync with the bus state. Context
        // menus aren't in the main visual tree so a normal binding doesn't
        // trigger; we update the header text imperatively here.
        viewModel.Power.PropertyChanged += OnPowerStateChanged;
        UpdateTrayPowerHeader(viewModel.Power.IsActive);
    }

    private void OnPowerStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(KhurramAudioRoute.Core.PowerService.IsActive)
            && e.PropertyName != nameof(KhurramAudioRoute.Core.PowerService.State))
            return;

        if (DataContext is MainViewModel vm)
            Dispatcher.BeginInvoke(() => UpdateTrayPowerHeader(vm.Power.IsActive));
    }

    private void UpdateTrayPowerHeader(bool isActive)
    {
        // ContextMenu lives in Window.Resources, so its child MenuItems aren't
        // generated as named fields. Walk the menu at runtime to find ours.
        if (TryResolveResource("TrayMenu") is not System.Windows.Controls.ContextMenu menu)
            return;

        foreach (var item in menu.Items)
        {
            if (item is System.Windows.Controls.MenuItem mi
                && mi.Name == nameof(TrayPowerMenuItem))
            {
                mi.Header = isActive ? "Turn audio bus OFF" : "Turn audio bus ON";
                return;
            }
        }
    }

    private object? TryResolveResource(string key)
    {
        try { return TryFindResource(key); }
        catch { return null; }
    }

    private const string TrayPowerMenuItem = "TrayPowerMenuItem";

    private void OnSessionEnding(object? sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.Power.DisengageOnShutdown();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        double minW = MinWidth > 0 ? MinWidth : 1024;
        double minH = MinHeight > 0 ? MinHeight : 700;
        double targetW = Math.Min(1350, workArea.Width * 0.82);
        double targetH = Math.Max(680, workArea.Height * 0.60);
        Width = Math.Max(minW, targetW);
        Height = Math.Max(minH, targetH);
        Width = Math.Min(Width, workArea.Width - 48);
        Height = Math.Min(Height, workArea.Height - 48);
        _meterTimer.Start();

        if (DataContext is MainViewModel vm)
        {
            Dispatcher.BeginInvoke(() => OfferVbCableStartupReminder(vm), DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>
    /// One modal per session when VB-CABLE is missing unless the user opts out permanently.
    /// </summary>
    private void OfferVbCableStartupReminder(MainViewModel vm)
    {
        if (_vbCableReminderShown) return;
        if (UserSettings.GetSuppressVbCableStartupReminder()) return;
        if (vm.Power.IsBusInstalled) return;

        _vbCableReminderShown = true;
        var dlg = new VbCableReminderDialog { Owner = this };
        dlg.ShowDialog();
        if (dlg.SuppressReminder)
            UserSettings.SetSuppressVbCableStartupReminder(true);
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

        // Last-chance hook: if the user closed the app while the bus was on,
        // restore their Windows default before the process exits.
        if (DataContext is MainViewModel vm)
        {
            vm.Power.DisengageOnShutdown();
            vm.Power.PropertyChanged -= OnPowerStateChanged;
        }

        Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
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

    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OnHyperlinkRequestNavigate: {ex.Message}");
        }
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
        if (sender is not FrameworkElement element)
            return;
        var preset = element.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(preset))
            return;
        ApplyMasterEqPresetUi(preset);
    }

    private void OnResetEq(object sender, RoutedEventArgs e) => ApplyMasterEqPresetUi("Flat");

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

    private void ApplyMasterEqPresetUi(string? preset)
    {
        if (DataContext is not MainViewModel vm || string.IsNullOrWhiteSpace(preset))
            return;
        vm.ApplyMasterEqPreset(preset);
    }

    private void OnEqPresetRadioClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || !IsLoaded || rb.Tag is not string key || string.IsNullOrWhiteSpace(key))
            return;
        ApplyMasterEqPresetUi(key);
    }

    private void OnSpatialPresetRadioClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || !IsLoaded || rb.Tag is not string tag || string.IsNullOrWhiteSpace(tag))
            return;
        if (!Enum.TryParse<SpatialPreset>(tag, ignoreCase: false, out var preset))
            return;
        if (DataContext is not MainViewModel vm)
            return;
        vm.MasterSpatialPreset = preset;
    }

    private void OnTrayIconDoubleClick(object sender, RoutedEventArgs e) => ShowWindow();

    /// <summary>
    /// Tray-menu version of the header power chip. Mirrors
    /// <see cref="MainViewModel.TogglePower"/> so both surfaces drive the
    /// same <see cref="PowerService"/> instance and stay in sync.
    /// </summary>
    private async void OnTrayPowerClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        try
        {
            if (vm.Power.IsActive)
                await vm.Power.DisengageAsync();
            else
                await vm.Power.EngageAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnTrayPowerClicked failed: {ex.Message}");
        }
    }
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

public class SpatialPresetEnumMatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is not string paramStr || !Enum.TryParse<SpatialPreset>(paramStr, out var needle))
            return false;
        if (value is SpatialPreset vp)
            return vp == needle;
        if (value != null && Enum.TryParse<SpatialPreset>(value.ToString(), out var parsed))
            return parsed == needle;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
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

/// <summary>Radio segmented EQ: compares <see cref="AudioDevice.SelectedEqPresetKey"/> to Tag string.</summary>
public class EqPresetKeyMatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return parameter?.ToString() ?? "Flat";
        return Binding.DoNothing;
    }
}

/// <summary>Returns true when a render endpoint friendly name hints at headphones/headset.</summary>
public class PlaybackNameSuggestsHeadphonesConverter : IValueConverter
{
    private static readonly string[] HeadphoneHints =
    [
        "headphone", "headset", "earphone", "earbud", "airpods",
        " buds", " xm4", " xm5",
    ];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s))
            return false;
        foreach (var h in HeadphoneHints)
        {
            if (s.Contains(h, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
