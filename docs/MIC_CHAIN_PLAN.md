# Mic Chain Plan (CABLE-B Voice Studio)

> **Scope: post-v1.** This document describes the planned voice
> studio chain that will sit on the **second** VB-CABLE (CABLE B)
> when the user has it installed. v1 ships playback-only; the
> Microphones page UI lands in a follow-up milestone.

## How to use it in Teams / Discord / Zoom / OBS *(important — read first)*

The voice chain only does anything if the receiving app picks the **virtual cable's output endpoint** as its microphone. SonicFlow does NOT replace your real mic at the Windows level — it captures from the real mic, processes, and writes to the virtual cable. Apps then choose between "real mic" (raw) or "CABLE-B Output" (processed).

**Required setup (one time):**

1. Install the VB-CABLE 2-cable bundle (free from VB-Audio). You'll see *CABLE-A Input / Output* and *CABLE-B Input / Output* show up in Windows.
2. In SonicFlow, open the **Mic Devices** tab → Voice Studio card.
3. **Microphone source** → pick your real mic (USB, headset, BT — whatever you actually speak into).
4. **Render to (virtual mic)** → pick **CABLE-B Input**. (If you only installed one cable, pick *CABLE Input*; the playback bus uses CABLE-A so this collides — install the bundle.)
5. Tap **Engage**. Status should read `Live: <real mic> → CABLE-B Input`. Both meters should move when you talk.

**In Teams:**

1. *Three-dot menu* → **Settings** → **Devices**.
2. Under **Microphone**, pick **CABLE-B Output** (NOT your real mic — that bypasses everything).
3. Hit *Make a test call* — you should hear yourself with the chain applied (gate + EQ + pitch + limiter).

**Same flow for Discord / Zoom / OBS** — just pick *CABLE-B Output* as the input device.

**Common mistakes:**

- **No effect heard** → Teams is still set to your real mic. Switch it to CABLE-B Output.
- **Silence in Teams** → SonicFlow's Voice Studio card is not Engaged, or Windows muted CABLE-B Input.
- **Echo / feedback** → you have CABLE-B Output as both your *Teams microphone* AND a *Windows playback default*. CABLE-B Output should only be used as an input device.
- **Crackling** → mic capture format mismatch. Try a different mic or set its Windows sample rate to 48 kHz / 16-bit shared.
- **Voice sounds doubled** → Teams' built-in noise suppression is fighting our gate. In Teams *Settings → Devices → Noise suppression*, set to **Low** or **Off** when SonicFlow is engaged.

**You do NOT need the playback bus on for the mic chain.** The voice chain is fully independent of master power.

---

## Goal

Give the user a polished "podcast" microphone with a small set of
high-impact effects, exposed to other apps as **CABLE-B Output**
(virtual mic). The user picks `CABLE-B Output` as the input device
in Discord / Zoom / OBS and hears the processed voice without
running anything else.

## Topology

```text
   Real microphone (any USB / 3.5 mm / BT mic)
                       │
                  WASAPI capture
                       │
   ┌───────────────────┴────────────────────┐
   │  VOICE PIPELINE (independent of bus)   │
   │   ▸ Noise gate                         │
   │   ▸ Optional ML denoise (RNNoise/onnx) │
   │   ▸ Pitch / formant shifter            │
   │   ▸ Voice "studio" EQ + saturation     │
   │   ▸ Soft limiter                       │
   └───────────────────┬────────────────────┘
                       │ WASAPI render
                       ▼
   CABLE-B Input  ──►  CABLE-B Output  ──►  apps pick this as their mic
```

The chain is fully independent of the playback bus. The two share no
state and run on separate threads.

## Effects (v2 / v3 split)

### v2 (after v1 ships)

- **Noise gate** with adjustable threshold + hold (NAudio sample
  provider; no external deps). **Phase 1 shipped:** `Core/Voice/NoiseGate.cs` (envelope-following with hysteresis, attack/release, hold), smoke-tested in `AudioCoreTester` (-12 dBFS tone passes, -60 dBFS noise suppressed below -50 dBFS RMS).
  **Phase 2 shipped:** `Core/MicChainEngine.cs` — WASAPI capture (10 ms event-driven) → mono down-mix → `BufferedWaveProvider` → `NoiseGate` stage → `WdlResamplingSampleProvider` → `MonoToStereoSampleProvider` → `WasapiOut` to a configurable render endpoint (CABLE-B Input). `Start(micId, renderId)` / `Stop()` / `IsRunning` / `IDisposable`; `NoiseGate` instance exposed for UI binding.
  **Phase 3 shipped:** `ViewModels/MicChainViewModel.cs` + Voice Studio card on the Microphones page (`MainWindow.xaml`). Mic-picker, render-picker (auto-selects CABLE-B Input when present), Engage/Disengage button, gate threshold + hold sliders. Persisted via new `UserSettings` accessors (`MicChainMicId`, `MicChainRenderId`, `MicChainEnabled`, `MicChainGateThresholdDb`, `MicChainGateHoldMs`). Live device list refreshed on each `RefreshData` cycle.
  **Phase 3b shipped:** input level meter on the Voice Studio card. `MicChainEngine.InputPeak` tracks per-callback peak with frame-to-frame decay; `MicChainViewModel.Pump()` mirrors it onto `InputPeak` (ObservableProperty), driven from `MainViewModel.RefreshMeters` (existing meter timer). Bar uses the existing `PeakToHeightConverter`.
  **Phase 4b shipped:** post-chain output level meter. `Core/Voice/PeakMonitor.cs` is a transparent `ISampleProvider` inserted after the soft limiter; `MicChainEngine.OutputPeak` exposes its decaying peak. Voice Studio card now shows two stacked bars (input + output) so users can see headroom into the limiter.
- **Voice studio EQ** — preset library: "Bright", "Warm", "Radio",
  "Telephone". **Phase 4 shipped:** `Core/Voice/VoiceEq.cs` — `ISampleProvider` with `VoiceEqPreset` enum (Off/Bright/Warm/Radio/Telephone). Each preset is a 3-stage `BiQuadFilter` chain (NAudio.Dsp). Voice Studio card has a preset ComboBox; persisted via `UserSettings.MicChainVoiceEqPreset`.
- **Soft limiter** (mirrors `SpatialMath.Limit` math). **Phase 4 shipped:** `Core/Voice/SoftLimiter.cs` — `ISampleProvider` applying clamp(±1.4) + tanh per sample, last stage of the chain. Chain order: capture → NoiseGate → VoiceEq → SoftLimiter → resample → stereo → render.
- **Pitch shifter (semitone-based)** via Cavern's offline filter or
  a SoundTouch.NET wrapper. Preserves formants when feasible.
  **Phase 5 shipped:** `Core/Voice/PitchShifter.cs` — `ISampleProvider` wrapping `SoundTouch.SoundTouchProcessor` (NuGet `SoundTouch.Net 2.3.2`). `Semitones` property [-12..+12]; bypass at 0 short-circuits SoundTouch entirely so there's zero startup latency / quality loss when off. Inserted between `VoiceEq` and `SoftLimiter`. Persisted via `UserSettings.MicChainPitchSemitones`. Pitch slider on the Voice Studio card.
  **Phase 6 shipped (UX polish):**
  - Voice EQ ComboBox replaced with a 5-pill grid (Off / Bright / Warm / Radio / Telephone) using the same `SonicSpatialPresetListRadio` style as the Outputs spatial pills, driven by a `OnVoiceEqRadioClick` handler + `VoiceEqPresetEnumMatchConverter`.
  - **Voice character** preset row added above EQ — `Core/Voice/VoiceCharacterPreset.cs` enum (None / Robot / Girl 1 / Girl 2 / Deep / Chipmunk) with a static `VoiceCharacterPresets.Resolve` map to a `(VoiceEqPreset, pitchSemis, gateThresholdDb, gateHoldMs)` bundle. Picking a character writes every slider in one click; touching any of those sliders afterwards clears the character back to None (`_applyingCharacter` flag prevents recursion). Persisted via `UserSettings.MicChainCharacterPreset`.
  - **Reset gate** button — `MicChainViewModel.ResetGateCommand` restores threshold = -40 dBFS, hold = 80 ms (defaults exposed as `DefaultGateThresholdDb`/`DefaultGateHoldMs` constants).
  **Phase 8 shipped (UI consolidation):** "Voice character" and "Voice EQ" pill rows merged into a single **Voice preset** row to remove the visual conflict where picking a character (e.g. Robot) lit up an EQ pill (Telephone) at the same time. New `Core/Voice/VoicePreset.cs` enum {None, Bright, Warm, Radio, Telephone, Robot, Girl1, Girl2, Deep, Chipmunk}; row 1 = EQ flavours, row 2 = full character macros. Picking **None** now genuinely resets the row (writes EQ=Off, pitch=0, gate defaults). `SelectedVoicePreset` is now `VoicePreset?` — null when the user has manually edited a slider so no pill is highlighted. Old `VoiceCharacterPreset` enum and its accessors / converter / handler deleted.

  **Phase 7 shipped (studio polish — A in user's brainstorm list):**
  - **Compressor** — `Core/Voice/Compressor.cs`. Mono peak-detection envelope, soft-knee curve, attack/release smoothing, makeup gain. Mirrors a standard radio/podcast voice compressor. Bypassed when not engaged.
  - **De-esser** — `Core/Voice/DeEsser.cs`. Side-chain band-pass biquad at 6.5 kHz; envelope follower; dynamic high-shelf cut scaled by over-threshold amount. Tames sibilance per-syllable instead of statically.
  - **Studio polish** macro presets — `Core/Voice/StudioPolishPreset.cs` enum (None / Soft / Strong / Broadcast) with a `StudioPolishPresets.Resolve` map producing a compressor + de-esser bundle. None = both bypassed; Soft = gentle 2:1 + light de-ess; Strong = 3:1 + +4 dB makeup + firm de-ess; Broadcast = aggressive 4:1 + +6 dB + heavier de-ess.
  - Chain order is now: capture → NoiseGate → VoiceEq → DeEsser → Compressor → PitchShifter → SoftLimiter → PeakMonitor → resample → stereo → render.
  - Persisted via `UserSettings.MicChainStudioPolish`. Pill row on Voice Studio card (4-wide UniformGrid). Character bundles do NOT touch Studio polish — the two layers are orthogonal so users can stack a "Girl 1" character with a "Broadcast" polish.
- **Persisted preferences** per Windows mic id.

### v3

- **ML denoise** via RNNoise (~ 80 KB ONNX model) fronting the chain.
  **Phase 10 shipped (C in user's brainstorm list — noise reduction):** `Core/Voice/SpectralDenoise.cs` — pure-C# spectral-subtraction noise suppressor (512-point FFT, 256-sample hop, Hann analysis/synthesis window, per-bin leaky-minimum noise floor tracker, magnitude subtraction with overestimate + soft floor). Latency ≈ 5 ms at 48 kHz. Note: this is the lighter spectral-subtraction approach, not actual RNNoise — handles stationary noise (fan / AC / room tone) but won't strip transient noise like keyboard clicks. RNNoise via ONNX runtime (~80 MB native binaries) remains a future v3 option for upgrade. `Core/Voice/NoiseReductionPreset.cs` enum {Off, Light, Medium, Strong} + `Resolve` bundle (Enabled, ReductionDb, Overestimate). Pill row at top of the Voice Studio card. Persisted via `UserSettings.MicChainNoiseReduction`. Inserted at the HEAD of the chain so the gate / EQ / compressor see a cleaner signal. Final chain order: capture → SpectralDenoise → NoiseGate → VoiceEq → DeEsser → Compressor → PitchShifter → VoiceReverb → SoftLimiter → PeakMonitor → resample → stereo → render.
- **Reverb** (small room / studio plate) using
  `ConvolutionRoomStage` from the existing spatial pipeline.
  **Phase 9 shipped (B in user's brainstorm list — reverb presets):** `Core/Voice/VoiceReverb.cs` — Freeverb-style mono Schroeder reverb (8 parallel low-passed comb filters + 4 series all-pass filters; tunings scaled from 44.1 kHz to the source's actual sample rate). Pure C#, no IR file deps. `Core/Voice/ReverbPreset.cs` enum {None, VoiceBooth, VocalPlate, StudioRoom, ConcertHall, Cathedral} + `ReverbPresets.Resolve` bundle map (Enabled, RoomSize, Damping, WetMix). Voice Studio card has a 6-pill row under Studio polish. Persisted via `UserSettings.MicChainReverb`. Inserted into the chain BETWEEN PitchShifter and SoftLimiter so reverb tail follows the pitch shift but the limiter still catches reverb peaks. Final chain order: capture → NoiseGate → VoiceEq → DeEsser → Compressor → PitchShifter → VoiceReverb → SoftLimiter → PeakMonitor → resample → stereo → render.
- **De-esser** (compressor side-chained on a high-shelf).
- **Compressor** (single-band, threshold/ratio/knee) for level
  consistency.
- **Output level meter + clip indicator** on the Mic card.

### v4 (stretch)

- Voice cloning / style transfer via on-device ML (e.g. Tortoise).
- Per-app routing (Discord gets the Radio preset; OBS gets a clean
  feed) — would tie back into the eventual Applications page.

## UI sketch (Microphones page)

```text
┌──────────────────────────────────────────────┐
│  Live mic                                    │
│  [Realtek USB Audio]      Level ▌▌▌▌▌▌      │
│                                              │
│  Voice studio                                │
│  ┌─────────────┬─────────────┬─────────────┐ │
│  │  Studio EQ  │   Pitch     │   Reverb    │ │
│  │  [─•───]    │   [──•──]   │   [────]    │ │
│  └─────────────┴─────────────┴─────────────┘ │
│                                              │
│  [● Active]  Apps see CABLE-B Output as mic  │
│  [Power]                                     │
└──────────────────────────────────────────────┘
```

## Class / file plan

- `Core/MicChainEngine.cs` (new) — orchestrates the WASAPI capture +
  effects chain + CABLE-B render.
- `Core/Voice/NoiseGate.cs` (new) — sample provider stage.
- `Core/Voice/PitchShifter.cs` (new) — wraps SoundTouch or Cavern.
- `Core/Voice/StudioEqProfile.cs` (new) — preset library tied into
  `EqualizerSampleProvider`.
- `ViewModels/MicChainViewModel.cs` (new).
- `MicChainPage.xaml` (new view) loaded into the Microphones nav.

## Latency budget for the mic chain

- WASAPI capture buffer: 10 ms
- Noise gate / EQ / limiter: < 1 ms each
- Pitch shifter (real-time, low-latency): 20–40 ms typical
- ML denoise (RNNoise): 10 ms
- Render to CABLE-B: 10 ms
- **Total round-trip target:** < 60 ms (acceptable for live calls)

If `Total > 80 ms` the UI surfaces a warning chip; users on flaky BT
mics can opt out of pitch shift / ML denoise.

## Out of scope for the mic plan

- Speaker bleed cancellation (echo cancellation across CABLE-B and
  the playback bus). Probably a v5 feature.
- Multi-mic mixing.
- Video capture / virtual camera.
