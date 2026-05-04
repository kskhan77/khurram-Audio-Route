using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KhurramAudioRoute.Core;
using KhurramAudioRoute.Core.Voice;

namespace KhurramAudioRoute.ViewModels;

/// <summary>
/// ViewModel for the Voice Studio card on the Microphones page. Wraps a
/// <see cref="MicChainEngine"/> instance, exposes mic / render pickers and
/// gate sliders, and persists user choices via <see cref="UserSettings"/>.
/// See <c>docs/MIC_CHAIN_PLAN.md</c>.
/// </summary>
public partial class MicChainViewModel : ObservableObject, IDisposable
{
    public const float DefaultGateThresholdDb = -40f;
    public const float DefaultGateHoldMs = 80f;

    private readonly MicChainEngine _engine = new();
    private bool _suppressPersist;
    private bool _applyingCharacter;
    private bool _disposed;

    public ObservableCollection<MicChainEndpoint> AvailableMics { get; } = new();
    public ObservableCollection<MicChainEndpoint> AvailableRenderTargets { get; } = new();

    [ObservableProperty]
    private MicChainEndpoint? selectedMic;

    [ObservableProperty]
    private MicChainEndpoint? selectedRender;

    [ObservableProperty]
    private bool isPowered;

    [ObservableProperty]
    private float gateThresholdDb = -40f;

    [ObservableProperty]
    private float gateHoldMs = 80f;

    [ObservableProperty]
    private string statusMessage = "Idle.";

    [ObservableProperty]
    private float inputPeak;

    [ObservableProperty]
    private float outputPeak;

    [ObservableProperty]
    private VoiceEqPreset selectedVoicePreset = VoiceEqPreset.Off;

    [ObservableProperty]
    private float pitchSemitones;

    [ObservableProperty]
    private VoiceCharacterPreset selectedCharacter = VoiceCharacterPreset.None;

    public IReadOnlyList<VoiceEqPreset> VoicePresets { get; } = new[]
    {
        VoiceEqPreset.Off, VoiceEqPreset.Bright, VoiceEqPreset.Warm,
        VoiceEqPreset.Radio, VoiceEqPreset.Telephone,
    };

    public IReadOnlyList<VoiceCharacterPreset> CharacterPresets { get; } = new[]
    {
        VoiceCharacterPreset.None, VoiceCharacterPreset.Robot, VoiceCharacterPreset.Girl1,
        VoiceCharacterPreset.Girl2, VoiceCharacterPreset.DeepVoice, VoiceCharacterPreset.Chipmunk,
    };

    public bool CanPower => SelectedMic is not null && SelectedRender is not null;

    /// <summary>Called from the meter timer in <c>MainViewModel.RefreshMeters</c>.</summary>
    public void Pump()
    {
        bool live = IsPowered;
        InputPeak = live ? _engine.InputPeak : 0f;
        OutputPeak = live ? _engine.OutputPeak : 0f;
    }

    public MicChainViewModel()
    {
        _suppressPersist = true;
        try
        {
            GateThresholdDb = UserSettings.GetMicChainGateThresholdDb();
            GateHoldMs = UserSettings.GetMicChainGateHoldMs();
            SelectedVoicePreset = UserSettings.GetMicChainVoiceEqPreset();
            PitchSemitones = UserSettings.GetMicChainPitchSemitones();
            SelectedCharacter = UserSettings.GetMicChainCharacterPreset();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MicChainVM: failed to load gate settings: {ex.Message}");
        }
        finally
        {
            _suppressPersist = false;
        }
    }

    /// <summary>
    /// Refresh the mic + render combo boxes from the live device list.
    /// Picks up persisted selections when the saved id is still present.
    /// </summary>
    public void RefreshDevices()
    {
        var capture = SafeEnumerate(() => DeviceManager.GetCaptureDevices());
        var render = SafeEnumerate(() => DeviceManager.GetRenderDevices());

        _suppressPersist = true;
        try
        {
            string? savedMic = UserSettings.GetMicChainMicId();
            string? savedRender = UserSettings.GetMicChainRenderId();

            ReplaceCollection(AvailableMics, capture.Where(d => !string.IsNullOrWhiteSpace(d.Id))
                                                     .Select(d => new MicChainEndpoint(d.Id!, d.Name ?? d.Id!)));
            ReplaceCollection(AvailableRenderTargets, render.Where(d => !string.IsNullOrWhiteSpace(d.Id))
                                                            .Select(d => new MicChainEndpoint(d.Id!, d.Name ?? d.Id!)));

            SelectedMic = ResolveSelection(AvailableMics, savedMic, preferDefaultId: capture.FirstOrDefault(d => d.IsDefault)?.Id);
            SelectedRender = ResolveSelection(AvailableRenderTargets, savedRender, preferDefaultId: PreferredRenderId(render));
        }
        finally
        {
            _suppressPersist = false;
        }

        OnPropertyChanged(nameof(CanPower));
    }

    [RelayCommand]
    private void TogglePower()
    {
        if (IsPowered)
        {
            StopEngine();
            IsPowered = false;
            return;
        }

        if (!CanPower)
        {
            StatusMessage = "Pick a mic and a render target first.";
            return;
        }

        StartEngine();
    }

    private void StartEngine()
    {
        try
        {
            _engine.Start(SelectedMic!.Id, SelectedRender!.Id);
            ApplyGateToEngine();
            ApplyVoicePresetToEngine();
            ApplyPitchToEngine();
            IsPowered = true;
            StatusMessage = $"Live: {SelectedMic.Name} → {SelectedRender.Name} ({_engine.CaptureSampleRate} Hz → {_engine.RenderSampleRate} Hz).";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MicChainVM: start failed: {ex.Message}");
            StatusMessage = $"Couldn't start: {ex.Message}";
            try { _engine.Stop(); } catch { }
            IsPowered = false;
        }
    }

    private void StopEngine()
    {
        try { _engine.Stop(); } catch (Exception ex) { Debug.WriteLine($"MicChainVM: stop failed: {ex.Message}"); }
        StatusMessage = "Idle.";
    }

    private void ApplyGateToEngine()
    {
        var gate = _engine.NoiseGate;
        if (gate is null) return;
        gate.OpenThreshold = NoiseGate.LinearFromDb(GateThresholdDb);
        gate.CloseThreshold = NoiseGate.LinearFromDb(GateThresholdDb - 6f);
        gate.HoldMs = GateHoldMs;
    }

    private void ApplyVoicePresetToEngine()
    {
        var eq = _engine.VoiceEq;
        if (eq is null) return;
        eq.Preset = SelectedVoicePreset;
    }

    private void ApplyPitchToEngine()
    {
        var ps = _engine.PitchShifter;
        if (ps is null) return;
        ps.Semitones = PitchSemitones;
    }

    partial void OnSelectedMicChanged(MicChainEndpoint? value)
    {
        OnPropertyChanged(nameof(CanPower));
        if (_suppressPersist) return;
        UserSettings.SetMicChainMicId(value?.Id);
        if (IsPowered)
        {
            StopEngine();
            IsPowered = false;
            StartEngine();
        }
    }

    partial void OnSelectedRenderChanged(MicChainEndpoint? value)
    {
        OnPropertyChanged(nameof(CanPower));
        if (_suppressPersist) return;
        UserSettings.SetMicChainRenderId(value?.Id);
        if (IsPowered)
        {
            StopEngine();
            IsPowered = false;
            StartEngine();
        }
    }

    partial void OnGateThresholdDbChanged(float value)
    {
        if (_suppressPersist) return;
        UserSettings.SetMicChainGateThresholdDb(value);
        ApplyGateToEngine();
        ClearCharacterIfManualEdit();
    }

    partial void OnGateHoldMsChanged(float value)
    {
        if (_suppressPersist) return;
        UserSettings.SetMicChainGateHoldMs(value);
        ApplyGateToEngine();
        ClearCharacterIfManualEdit();
    }

    partial void OnSelectedVoicePresetChanged(VoiceEqPreset value)
    {
        if (_suppressPersist) return;
        UserSettings.SetMicChainVoiceEqPreset(value);
        ApplyVoicePresetToEngine();
        ClearCharacterIfManualEdit();
    }

    partial void OnPitchSemitonesChanged(float value)
    {
        if (_suppressPersist) return;
        UserSettings.SetMicChainPitchSemitones(value);
        ApplyPitchToEngine();
        ClearCharacterIfManualEdit();
    }

    partial void OnSelectedCharacterChanged(VoiceCharacterPreset value)
    {
        if (_suppressPersist) return;
        UserSettings.SetMicChainCharacterPreset(value);
        if (value == VoiceCharacterPreset.None) return;

        // Apply the bundle to every slider/preset. _applyingCharacter prevents
        // those slider OnXxxChanged hooks from clearing the character right back.
        var bundle = VoiceCharacterPresets.Resolve(value);
        _applyingCharacter = true;
        try
        {
            SelectedVoicePreset = bundle.Eq;
            PitchSemitones = bundle.PitchSemitones;
            GateThresholdDb = bundle.GateThresholdDb;
            GateHoldMs = bundle.GateHoldMs;
        }
        finally
        {
            _applyingCharacter = false;
        }
    }

    [RelayCommand]
    private void ResetGate()
    {
        GateThresholdDb = DefaultGateThresholdDb;
        GateHoldMs = DefaultGateHoldMs;
    }

    private void ClearCharacterIfManualEdit()
    {
        if (_applyingCharacter) return;
        if (SelectedCharacter == VoiceCharacterPreset.None) return;
        SelectedCharacter = VoiceCharacterPreset.None;
    }

    partial void OnIsPoweredChanged(bool value)
    {
        if (_suppressPersist) return;
        try { UserSettings.SetMicChainEnabled(value); } catch { }
    }

    private static IReadOnlyList<AudioDevice> SafeEnumerate(Func<List<AudioDevice>> get)
    {
        try { return get() ?? new List<AudioDevice>(); }
        catch (Exception ex)
        {
            Debug.WriteLine($"MicChainVM: device enumeration failed: {ex.Message}");
            return new List<AudioDevice>();
        }
    }

    private static void ReplaceCollection(ObservableCollection<MicChainEndpoint> target, IEnumerable<MicChainEndpoint> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private static MicChainEndpoint? ResolveSelection(IReadOnlyList<MicChainEndpoint> options, string? savedId, string? preferDefaultId)
    {
        if (options.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(savedId))
        {
            var hit = options.FirstOrDefault(o => string.Equals(o.Id, savedId, StringComparison.Ordinal));
            if (hit is not null) return hit;
        }
        if (!string.IsNullOrWhiteSpace(preferDefaultId))
        {
            var hit = options.FirstOrDefault(o => string.Equals(o.Id, preferDefaultId, StringComparison.Ordinal));
            if (hit is not null) return hit;
        }
        return options[0];
    }

    private static string? PreferredRenderId(IReadOnlyList<AudioDevice> render)
    {
        // Prefer the second virtual cable (CABLE-B Input) if one exists; fall
        // back to anything with "CABLE" in the name; otherwise nothing —
        // user must pick.
        var byName = render.FirstOrDefault(d => d.Name?.IndexOf("CABLE-B", StringComparison.OrdinalIgnoreCase) >= 0);
        if (byName is not null) return byName.Id;
        var anyCable = render.FirstOrDefault(d => d.Name?.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0);
        return anyCable?.Id;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _engine.Dispose(); } catch { }
    }
}

/// <summary>Combo-box row for the mic / render pickers.</summary>
public sealed record MicChainEndpoint(string Id, string Name)
{
    public override string ToString() => Name;
}
