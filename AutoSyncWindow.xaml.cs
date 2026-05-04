using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using KhurramAudioRoute.Core.SyncCalibration.L3;
using Wpf.Ui.Controls;

namespace KhurramAudioRoute;

/// <summary>L3 mic-based auto-sync wizard — <c>docs/LATENCY_PLAN.md</c>.</summary>
public partial class AutoSyncWindow : FluentWindow
{
    private readonly IReadOnlyList<AutoSyncRunner.Target> _targets;
    private readonly ObservableCollection<RowVm> _rows = new();
    private readonly Dictionary<string, RowVm> _rowsByDeviceId = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private bool _busy;

    /// <summary>Populated only when the user clicks Apply on a successful run.</summary>
    public AutoSyncRunner.Result? AppliedResult { get; private set; }

    public AutoSyncWindow(IReadOnlyList<AutoSyncRunner.Target> targets)
    {
        InitializeComponent();
        _targets = targets ?? Array.Empty<AutoSyncRunner.Target>();

        foreach (var t in _targets)
        {
            var vm = new RowVm
            {
                DeviceId = t.DeviceId,
                DeviceName = t.DeviceName,
                StatusBadge = "—",
                BadgeBrush = SonicColors.Idle,
                DetailLine = "Pending",
            };
            _rows.Add(vm);
            _rowsByDeviceId[t.DeviceId] = vm;
        }
        ResultsList.ItemsSource = _rows;

        if (_targets.Count == 0)
        {
            StatusText.Text = "No ACTIVE hardware outputs to calibrate.";
            StartBtn.IsEnabled = false;
        }
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        StartBtn.IsEnabled = false;
        ApplyBtn.IsEnabled = false;
        ErrorText.Text = string.Empty;
        StatusText.Text = "Starting…";

        foreach (var r in _rows)
        {
            r.StatusBadge = "—";
            r.BadgeBrush = SonicColors.Idle;
            r.DetailLine = "Pending";
            r.NotifyAll();
        }

        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => StatusText.Text = msg);
        try
        {
            var result = await Task.Run(() => AutoSyncRunner.RunAsync(_targets, progress, _cts.Token));
            ApplyResultToRows(result);

            int accepted = result.Targets.Count(t => t.Accepted);
            int rejected = result.Targets.Count - accepted;
            StatusText.Text = result.Success
                ? $"All {accepted} target(s) measured successfully. Review and Apply."
                : $"{accepted} accepted, {rejected} rejected. Apply will only adjust accepted devices.";

            if (!result.Success && !string.IsNullOrWhiteSpace(result.FailureReason))
                ErrorText.Text = result.FailureReason;

            ApplyBtn.IsEnabled = accepted > 0;
            AppliedResult = null; // not yet applied
            CachedResult = result;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Auto-sync stopped.";
            ErrorText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            StartBtn.IsEnabled = true;
            StartBtn.Content = "Re-run";
            _cts?.Dispose();
            _cts = null;
        }
    }

    private AutoSyncRunner.Result? CachedResult;

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (CachedResult is null) return;
        AppliedResult = CachedResult;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_busy) { _cts?.Cancel(); return; }
        DialogResult = false;
        Close();
    }

    private void ApplyResultToRows(AutoSyncRunner.Result result)
    {
        foreach (var t in result.Targets)
        {
            if (!_rowsByDeviceId.TryGetValue(t.DeviceId, out var row)) continue;
            if (t.Accepted)
            {
                row.StatusBadge = "Accepted";
                row.BadgeBrush = SonicColors.Accepted;
                row.DetailLine = $"raw {t.RawLagMs} ms → offset {t.NormalisedOffsetMs} ms · SNR {t.SnrDb:F1} dB";
            }
            else
            {
                row.StatusBadge = "Rejected";
                row.BadgeBrush = SonicColors.Rejected;
                row.DetailLine = t.RejectReason ?? "Unknown reason";
            }
            row.NotifyAll();
        }
    }

    private static class SonicColors
    {
        public static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xA2));
        public static readonly Brush Accepted = new SolidColorBrush(Color.FromRgb(0x66, 0xD9, 0x9A));
        public static readonly Brush Rejected = new SolidColorBrush(Color.FromRgb(0xF0, 0x80, 0x80));
        static SonicColors()
        {
            Idle.Freeze();
            Accepted.Freeze();
            Rejected.Freeze();
        }
    }

    private sealed class RowVm : INotifyPropertyChanged
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string StatusBadge { get; set; } = "";
        public Brush BadgeBrush { get; set; } = SonicColors.Idle;
        public string DetailLine { get; set; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;
        public void NotifyAll()
        {
            var h = PropertyChanged;
            if (h is null) return;
            h(this, new PropertyChangedEventArgs(nameof(StatusBadge)));
            h(this, new PropertyChangedEventArgs(nameof(BadgeBrush)));
            h(this, new PropertyChangedEventArgs(nameof(DetailLine)));
        }
    }
}
