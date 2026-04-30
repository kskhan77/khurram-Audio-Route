using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace KhurramAudioRoute.Controls;

// Lightweight animated spectrum visualization. Driven by a 0..1 Level dependency
// property (typically bound to PeakValue) — when level rises, bars become taller
// and more vivid. Idle level keeps a low ambient wave so cards never look "dead".
//
// Per-bar rainbow palette: each bar gets its own hue across the spectrum
// (magenta → red → orange → yellow → green → cyan), with a vertical brightness
// gradient inside each bar.
public partial class SpectrumMeter : UserControl
{
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(nameof(Level), typeof(float), typeof(SpectrumMeter),
            new PropertyMetadata(0f));

    public static readonly DependencyProperty BarCountProperty =
        DependencyProperty.Register(nameof(BarCount), typeof(int), typeof(SpectrumMeter),
            new PropertyMetadata(14, OnBarCountChanged));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(SpectrumMeter),
            new PropertyMetadata(true));

    public float Level
    {
        get => (float)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public int BarCount
    {
        get => (int)GetValue(BarCountProperty);
        set => SetValue(BarCountProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private readonly List<Border> _bars = new();
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private double[] _phaseOffsets = Array.Empty<double>();
    private double[] _multipliers = Array.Empty<double>();
    private double[] _smoothed = Array.Empty<double>();
    private double _phase;

    public SpectrumMeter()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildBars();
        _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
    }

    private static void OnBarCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpectrumMeter meter && meter.IsLoaded)
            meter.BuildBars();
    }

    private void BuildBars()
    {
        BarsHost.Children.Clear();
        BarsHost.Columns = Math.Max(1, BarCount);
        _bars.Clear();
        _phaseOffsets = new double[BarCount];
        _multipliers = new double[BarCount];
        _smoothed = new double[BarCount];

        for (int i = 0; i < BarCount; i++)
        {
            _phaseOffsets[i] = _rng.NextDouble() * Math.PI * 2;
            // Bell-shaped frequency response: middle bands taller than the edges
            double normIdx = BarCount > 1 ? (double)i / (BarCount - 1) : 0.5;
            _multipliers[i] = 0.55 + 0.45 * Math.Sin(normIdx * Math.PI);

            var bar = new Border
            {
                Margin = new Thickness(1.4, 0, 1.4, 0),
                CornerRadius = new CornerRadius(3, 3, 1.5, 1.5),
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = 2,
                MinHeight = 2,
                Background = CreateRainbowBarBrush(i, BarCount)
            };
            BarsHost.Children.Add(bar);
            _bars.Add(bar);
        }
    }

    // Per-bar rainbow with vertical brightness fade — bright top, slightly darker bottom.
    private static LinearGradientBrush CreateRainbowBarBrush(int barIndex, int totalBars)
    {
        double hueFraction = totalBars > 1 ? (double)barIndex / (totalBars - 1) : 0.0;
        // Sweep across magenta(300) → red(0/360) → orange(30) → yellow(60) → green(120) → cyan(180) → purple(280)
        // Going 280..580 mod 360 hits magenta-red-orange-yellow-green-cyan-blue
        double hue = (280 + hueFraction * 300) % 360;

        Color top = HsvToRgb(hue, 0.85, 1.00);
        Color mid = HsvToRgb(hue, 0.95, 0.92);
        Color bottom = HsvToRgb(hue, 1.00, 0.55);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop(top, 0.0));
        brush.GradientStops.Add(new GradientStop(mid, 0.55));
        brush.GradientStops.Add(new GradientStop(bottom, 1.0));
        brush.Freeze();
        return brush;
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255.0),
            (byte)Math.Round((g + m) * 255.0),
            (byte)Math.Round((b + m) * 255.0));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_bars.Count == 0)
            return;

        _phase += 0.26;
        double maxHeight = Math.Max(8, ActualHeight - 2);
        double level = Math.Clamp(Level, 0.0, 1.0);

        // Square-root boost so mid-low levels look proportionally bigger and the
        // bars feel "punchier" without needing very loud audio to register.
        double boostedLevel = Math.Sqrt(level);
        if (!IsActive) boostedLevel *= 0.05;

        for (int i = 0; i < _bars.Count; i++)
        {
            double wave = 0.5 + 0.5 * Math.Sin(_phase + _phaseOffsets[i] + i * 0.32);
            double idle = 0.05 + 0.04 * wave;
            double active = (0.20 + 0.95 * wave) * _multipliers[i];

            double target = idle * (1.0 - boostedLevel) + active * boostedLevel;
            // Extra punch on top — when boostedLevel approaches 1 add a 25% gain
            target *= 1.0 + 0.25 * boostedLevel;
            target = Math.Min(1.0, target);

            // Fast attack, slow release — peaks register dramatically, then taper
            double smoothing = target > _smoothed[i] ? 0.55 : 0.18;
            _smoothed[i] += (target - _smoothed[i]) * smoothing;

            _bars[i].Height = Math.Max(2, _smoothed[i] * maxHeight);
            _bars[i].Opacity = IsActive ? 0.6 + 0.4 * boostedLevel : 0.22;
        }
    }
}
