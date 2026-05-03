# Mic Chain Plan (CABLE-B Voice Studio)

> **Scope: post-v1.** This document describes the planned voice
> studio chain that will sit on the **second** VB-CABLE (CABLE B)
> when the user has it installed. v1 ships playback-only; the
> Microphones page UI lands in a follow-up milestone.

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
  provider; no external deps).
- **Voice studio EQ** — preset library: "Bright", "Warm", "Radio",
  "Telephone".
- **Soft limiter** (reuse `SoftLimiterStage` from
  `Core/Spatial/SpatialPipeline.cs`).
- **Pitch shifter (semitone-based)** via Cavern's offline filter or
  a SoundTouch.NET wrapper. Preserves formants when feasible.
- **Persisted preferences** per Windows mic id.

### v3

- **ML denoise** via RNNoise (~ 80 KB ONNX model) fronting the chain.
- **Reverb** (small room / studio plate) using
  `ConvolutionRoomStage` from the existing spatial pipeline.
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
