using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;
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
