using KhurramAudioRoute.Core;
using KhurramAudioRoute.Core.SyncCalibration;
using System.Windows;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Wpf.Ui.Controls;

namespace KhurramAudioRoute;

/// <summary>L2 3-tap perceptual sync wizard — <c>docs/LATENCY_PLAN.md</c>.</summary>
public partial class SyncCalibrationWindow : FluentWindow
{
    private readonly string _referenceDeviceId;
    private readonly IReadOnlyList<AudioDevice> _targets;
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource<CalChoice?>? _choiceGate;

    private enum CalChoice { Earlier, InSync, Later }

    public SyncCalibrationWindow(string referenceDeviceId, IReadOnlyList<AudioDevice> targets)
    {
        InitializeComponent();
        _referenceDeviceId = referenceDeviceId;
        _targets = targets;
        Loaded += (_, _) => _ = RunWizardAsync();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _cts.Cancel();
        _choiceGate?.TrySetResult(null);
        base.OnClosing(e);
    }

    private async Task RunWizardAsync()
    {
        try
        {
            int n = _targets.Count;
            for (int ti = 0; ti < n; ti++)
            {
                AudioDevice dut = _targets[ti];
                for (int step = 0; step < 3; step++)
                {
                    HeaderText.Text = $"Sync calibration — {dut.Name}";
                    StepText.Text = $"Output {ti + 1} of {n} · Step {step + 1} of 3";
                    PlaybackHint.Text = "Playing clicks…";
                    SetChoiceEnabled(false);

                    await SyncClickPlayer.PlayRefMiddleRefAsync(_referenceDeviceId, dut.Id!, _cts.Token)
                        .ConfigureAwait(true);

                    if (_cts.IsCancellationRequested)
                        return;

                    PlaybackHint.Text = "Which best matches what you heard?";
                    _choiceGate = new TaskCompletionSource<CalChoice?>();
                    SetChoiceEnabled(true);

                    var choice = await _choiceGate.Task.ConfigureAwait(true);
                    if (choice is null || _cts.IsCancellationRequested)
                        return;

                    ApplyStep(choice.Value, dut);
                }

                if (SyncClickPlayer.TryCaptureFingerprint(dut.Id!, out var fp))
                    UserSettings.SetL2Calibration(dut.Id!, fp);
            }

            System.Windows.MessageBox.Show(
                "Sync calibration finished. Your per-output Sync values were updated.",
                "SonicFlow",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            // closing
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Sync wizard stopped: {ex.Message}",
                "SonicFlow",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            Close();
        }
    }

    private static void ApplyStep(CalChoice choice, AudioDevice dut)
    {
        int delta = choice switch
        {
            CalChoice.Earlier => SyncCalibrationPlanner.OffsetStepMs,
            CalChoice.Later => -SyncCalibrationPlanner.OffsetStepMs,
            _ => 0
        };

        if (delta == 0)
            return;

        int next = dut.TargetLatencyOffsetMs + delta;
        next = Math.Clamp(next, SyncCalibrationPlanner.MinBridgeOffsetMs, SyncCalibrationPlanner.MaxBridgeOffsetMs);
        dut.TargetLatencyOffsetMs = next;
    }

    private void SetChoiceEnabled(bool on)
    {
        EarlierBtn.IsEnabled = on;
        SyncBtn.IsEnabled = on;
        LaterBtn.IsEnabled = on;
    }

    private void OnEarlierClick(object sender, RoutedEventArgs e)
        => _choiceGate?.TrySetResult(CalChoice.Earlier);

    private void OnSyncClick(object sender, RoutedEventArgs e)
        => _choiceGate?.TrySetResult(CalChoice.InSync);

    private void OnLaterClick(object sender, RoutedEventArgs e)
        => _choiceGate?.TrySetResult(CalChoice.Later);
}
