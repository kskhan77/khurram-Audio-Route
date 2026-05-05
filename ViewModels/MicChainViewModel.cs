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
    private bool _applyingPreset;
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
    private VoicePreset? selectedVoicePreset;

    [ObservableProperty]
    private float pitchSemitones;

    [ObservableProperty]
    private StudioPolishPreset selectedStudioPolish = StudioPolishPreset.None;

    [ObservableProperty]
    private ReverbPreset selectedReverb = ReverbPreset.None;

    [ObservableProperty]
    private NoiseReductionPreset selectedNoiseReduction = NoiseReductionPreset.Off;

    public IReadOnlyList<VoicePreset> VoicePresetsList { get; } = new[]
    {
        VoicePreset.None,      VoicePreset.Bright, VoicePreset.Warm,  VoicePreset.Radio,    VoicePreset.Telephone,
        VoicePreset.Robot,     VoicePreset.Girl1,  VoicePreset.Girl2, VoicePreset.Deep,     VoicePreset.Chipmunk,
    };

    public IReadOnlyList<StudioPolishPreset> StudioPolishPresetsList { get; } = new[]
    {
        StudioPolishPreset.None, StudioPolishPreset.Soft, StudioPolishPreset.Strong, StudioPolishPreset.Broadcast,
    };

    public IReadOnlyList<ReverbPreset> ReverbPresetsList { get; } = new[]
    {
        ReverbPreset.None, ReverbPreset.VoiceBooth, ReverbPreset.VocalPlate,
        ReverbPreset.StudioRoom, ReverbPreset.ConcertHall, ReverbPreset.Cathedral,
    };

    public IReadOnlyList<NoiseReductionPreset> NoiseReductionPresetsList { get; } = new[]
    {
        NoiseReductionPreset.Off, NoiseReductionPreset.Light,
        NoiseReductionPreset.Medium, NoiseReductionPreset.Strong,
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
            SelectedVoicePreset = UserSettings.GetMicChainVoicePreset();
            PitchSemitones = UserSettings.GetMicChainPitchSemitones();
            SelectedStudioPolish = UserSettings.GetMicChainStudioPolish();
            SelectedReverb = UserSettings.GetMicChainReverb();
            SelectedNoiseReduction = UserSettings.GetMicChainNoiseReduction();
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
            ApplyStudioPolishToEngine();
            ApplyReverbToEngine();
            ApplyNoiseReductionToEngine();
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
        // The unified VoicePreset only writes the EQ stage's preset. Pitch and
        // gate values are owned by their own sliders and persisted separately;
        // when a preset is picked, the bundle-apply hook in
        // OnSelectedVoicePresetChanged updates those sliders too.
        var eq = _engine.VoiceEq;
        if (eq is null) return;
        var preset = SelectedVoicePreset;
        var bundle = preset is null
            ? new VoicePresetBundle(VoiceEqPreset.Off, 0f, DefaultGateThresholdDb, DefaultGateHoldMs)
            : VoicePresets.Resolve(preset.Value);
        eq.Preset = bundle.Eq;
    }

    private void ApplyPitchToEngine()
    {
        var ps = _engine.PitchShifter;
        if (ps is null) return;
        ps.Semitones = PitchSemitones;
    }

    private void ApplyReverbToEngine()
    {
        var rev = _engine.Reverb;
        if (rev is null) return;
        var bundle = ReverbPresets.Resolve(SelectedReverb);
        rev.Enabled = bundle.Enabled;
        rev.RoomSize = bundle.RoomSize;
        rev.Damping = bundle.Damping;
        rev.WetMix = bundle.WetMix;
    }

    private void ApplyNoiseReductionToEngine()
    {
        var dn = _engine.Denoise;
        if (dn is null) return;
        var bundle = NoiseReductionPresets.Resolve(SelectedNoiseReduction);
        dn.Enabled = bundle.Enabled;
        dn.ReductionDb = bundle.ReductionDb;
        dn.Overestimate = bundle.Overestimate;
    }

    private void ApplyStudioPolishToEngine()
    {
        var bundle = StudioPolishPresets.Resolve(SelectedStudioPolish);

        var comp = _engine.Compressor;
        if (comp is not null)
        {
            comp.Enabled = bundle.CompressorEnabled;
            comp.ThresholdDb = bundle.CompressorThresholdDb;
            comp.Ratio = bundle.CompressorRatio;
            comp.MakeupDb = bundle.CompressorMakeupDb;
        }

        var deEss = _engine.DeEsser;
        if (deEss is not null)
        {
            deEss.Enabled = bundle.DeEsserEnabled;
            deEss.ThresholdDb = bundle.DeEsserThresholdDb;
            deEss.MaxReductionDb = bundle.DeEsserMaxReductionDb;
        }
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
        ClearVoicePresetIfManualEdit();
    }

    partial void OnGateHoldMsChanged(float value)
    {
        if (_suppressPersist) return;
        UserSettings.SetMicChainGateHoldMs(value);
        ApplyGateToEngine();
        ClearVoicePresetIfManualEdit();
    }

    partial void OnPitchSemitonesChanged(float value)
    {
        if (_suppressPersist) return;
        UserSettings.SetMicChainPitchSemitones(value);
        ApplyPitchToEngine();
        ClearVoicePresetIfManualEdit();
    }

    partial void OnSelectedStudioPolishChanged(StudioPolishPreset value)
    {
        if (_suppressPersist) return;
        UserSettings.SetMicChainStudioPolish(value);
        ApplyStudioPolishToEngine();
    }

    partial void OnSelectedReverbChanged(ReverbPreset value)
    {
        if (_suppressPersist) return;
        UserSettings.SetMicChainReverb(value);
        ApplyReverbToEngine();
    }

    partial void OnSelectedNoiseReductionChanged(NoiseReductionPreset value)
    {
        if (_suppressPersist) return;
        UserSettings.SetMicChainNoiseReduction(value);
        ApplyNoiseReductionToEngine();
    }

    partial void OnSelectedVoicePresetChanged(VoicePreset? value)
    {
        if (_suppressPersist) return;
        UserSettings.SetMicChainVoicePreset(value);

        // Every preset writes the engine's EQ stage and the slider-bound EQ /
        // pitch / gate values. Picking "None" therefore acts as a real reset.
        if (value is null) { ApplyVoicePresetToEngine(); return; }

        var bundle = VoicePresets.Resolve(value.Value);
        _applyingPreset = true;
        try
        {
            PitchSemitones = bundle.PitchSemitones;
            GateThresholdDb = bundle.GateThresholdDb;
            GateHoldMs = bundle.GateHoldMs;
        }
        finally
        {
            _applyingPreset = false;
        }
        ApplyVoicePresetToEngine();
    }

    [RelayCommand]
    private void ResetGate()
    {
        GateThresholdDb = DefaultGateThresholdDb;
        GateHoldMs = DefaultGateHoldMs;
    }

    private void ClearVoicePresetIfManualEdit()
    {
        if (_applyingPreset) return;
        if (SelectedVoicePreset is null) return;
        SelectedVoicePreset = null;
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
