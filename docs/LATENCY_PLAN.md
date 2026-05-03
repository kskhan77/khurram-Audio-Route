# Latency Plan (Outputs / playback path)

Latency is the single biggest correctness problem when mirroring one
captured stream onto multiple physical outputs. This document tracks
the staged roadmap from "manual sliders that work" all the way to
"mic-based auto-sync".

The mic chain (CABLE-B voice studio) has its own latency concerns and
lives in [`MIC_CHAIN_PLAN.md`](MIC_CHAIN_PLAN.md). The numbers below
apply to the playback fan-out only.

## Inventory of existing latency knobs

| Knob | Purpose | Default |
|---|---|---|
| `BaselinePreDelayMs = 250` (`Core/DuplicationManager.cs`) | Headroom so user-visible "0 ms" sits in the middle of the slider range and negative offsets work. | 250 ms |
| `AudioDevice.SourceLatencyMs` | Pre-fan-out delay applied uniformly to every mirror target. Used to align the bus copy with the source's natural OS playback. | 0 ms |
| `DeviceSelection.LatencyOffsetMs` | Per-target offset relative to the baseline. | 0 ms |
| `TargetPlaybackLatencyMs = 45` (NAudio mirror path) | NAudio `WasapiOut` shared-mode buffer. | 45 ms |
| `TargetBufferDuration = 180 ms` | Per-target ring-buffer in `BufferedWaveProvider`. | 180 ms |
| BASS bridge (`Core/BassEngine.cs`) buffer | `WasapiInitFlags.Buffer` + `0.1f / 0.05f` periods. | ~50 ms |

The user-facing concept on every output card is the **Sync** slider,
which writes `LatencyOffsetMs` for that target. We intentionally do
not expose the others to keep the UI manageable.

## Phases

### L1 — In v1 (baseline) — **shipped**

| Feature | Notes |
|---|---|
| **Device-class default offsets** | Heuristic: derive a class from `MMDevice.IconPath` + friendly name. BT/A2DP → +30 ms, HDMI → +10 ms, USB DAC → 0 ms, onboard → 0 ms. Applied **only** the first time a device is seen; user edits override permanently. |
| **Total perceived latency banner** | Bus chip popover shows `≈ X ms total = baseline + sourceLatency + maxTargetOffset + WasapiOut buffer`. Updates live as the user moves any slider. |
| **Adaptive `TargetPlaybackLatencyMs`** | USB / HDMI / onboard → 25 ms. BT → 80 ms. Reduces underruns on flaky BT links without raising baseline for everyone. |
| **`UserSettings.LatencyClassDefaults`** | Persists the class → ms map so changes propagate to new devices. |
| **Class chip on each device card** | Tiny tag next to the device name (`BT`, `HDMI`, `USB`, `On-board`). Helps the user understand why one slider sits at +30 ms. |

Acceptance:

- Plugging a Bluetooth headset that the app has never seen yields a
  sensible default offset without manual tweaking.
- Hovering the bus chip shows a single-line total latency that
  reflects the slider state in real time.

### L2 — Perceptual wizard — **shipped (v1)**

**Code:** `Core/SyncCalibration/*`, `SyncCalibrationWindow.xaml`, `MainViewModel.OpenSyncCalibrationWizard`.

| Item | Implementation |
|---|---|
| Click | ~48 ms sine burst, Hann window, PCM 48 kHz stereo via `WasapiOut` (`SyncClickPlayer`). |
| Pattern | **Reference — device under test — reference** with ~260 ms gaps. |
| Buttons | **Earlier** / **In sync** / **Later** ⇒ adjust DUT `TargetLatencyOffsetMs` by **±20 ms** (clamp `0 … 120`), **3** steps per ACTIVE output. |
| Reference | Backup mode → Windows **multimedia** default render id. Bus mode → ACTIVE physical device with **minimum** current Sync offset. |
| Persist | `UserSettings` stores per-endpoint `UtcTicks` + WASAPI **mix fingerprint** (`L2SyncCalibrationRow`). |

UI sketch:

```text
┌──────────────────────────────────────────────┐
│  Sync calibration — Sony WH-1000XM5          │
│                                              │
│  Playing a click... did you hear it          │
│  before, in sync, or after the speakers?     │
│                                              │
│  [ ▼ Earlier ]   [ ✓ In sync ]   [ ▲ Later ] │
│                                              │
│  Step 2 of 3                                 │
└──────────────────────────────────────────────┘
```

**Optional polish (not blocking):**

- Badge / tooltip when the live fingerprint differs from the last saved calibration (auto re-prompt spec in original outline).

### L3 — Mic-based auto-sync — **code-complete (pending hardware QA)**

**Code:** `Core/SyncCalibration/L3/*`, `AutoSyncWindow.xaml`, `MainViewModel.OpenAutoSyncWizard`.

| Item | Implementation |
|---|---|
| Probe | 250 ms log-sweep 200 Hz → 12 kHz, IEEE float, Hann fade in/out (`LogSweepGenerator`). |
| Capture | `WasapiCapture` from default mic; mono down-mix at native rate (`MicCapture`). |
| Math | Radix-2 FFT cross-correlation, peak lag + SNR (`CrossCorrelator`). Smoke-tested in `AudioCoreTester` against a synthetic delayed sweep at lag = 137 samples. |
| Per-target playback | One-shot `WasapiOut` to the DUT (`SweepPlayer`), mirrors `SyncClickPlayer` pattern. |
| Orchestration | `AutoSyncRunner.RunAsync` walks ACTIVE physical targets; sanity gates on lag ∈ [0, 500] ms and SNR ≥ 9 dB; normalises so earliest = 0. |
| UI | **Auto-sync** button on Outputs (next to **Sync wizard**), `AutoSyncWindow` modal with Start / Apply / Cancel and per-target Accepted / Rejected rows. |
| Persist | `UserSettings.SetL3AutoSync(deviceId, offsetMs, snrDb)`; "Auto-synced N days ago" chip on each card via `AudioDevice.AutoSyncCaption` + `L3AutoSyncCaption.Refresh`. |

UI sketch:

```text
┌──────────────────────────────────────────────┐
│  Auto-sync (L3)                              │
│                                              │
│  [Sony WH-1000XM5]   Accepted  raw 32 ms…    │
│  [Living-room TV]    Accepted  raw 18 ms…    │
│  [USB DAC]           Rejected  SNR too low   │
│                                              │
│  [ Start ] [ Apply ] [ Cancel ]              │
└──────────────────────────────────────────────┘
```

**Next polish (not blocking):**

- ~~L3.b drift watchdog~~ — **shipped:** `AudioDevice.AutoSyncDrift` + `DRIFT?` chip; `L3AutoSyncCaption.Refresh` reuses `SyncClickPlayer.TryCaptureFingerprint` to compare saved vs live mix format.
- ~~Auto-mute non-DUT outputs during measurement~~ — **shipped:** `OutputMuteScope` snapshots and restores `AudioEndpointVolume.Mute` on every non-DUT target around each `MeasureOneAsync` window.

#### Mic-based auto-sync (chirp + cross-correlation) — original spec

Goal: zero manual sliders. Press a button, the app figures it out.

1. **Generate a probe.** ~250 ms log-sweep chirp from 200 Hz → 12 kHz
   (Cavern's filters can do this; alternatively use NAudio
   `SignalGenerator`). Energy is shaped to be audible without being
   alarming.
2. **Capture from system mic** for ~1.5 s starting just before the
   chirp plays.
3. **For each `[● Active]` target:** schedule the chirp on that
   target only (mute the others briefly), record from the mic, and
   compute time-of-flight via FFT-based cross-correlation between
   the captured signal and the known reference.
4. **Apply** offsets so the **earliest-arriving** target becomes
   the reference (offset 0) and the others get positive offsets to
   match it.
5. **Sanity gates:** reject results outside `[0, 500] ms`; surface
   "couldn't hear it — boost speaker volume / move closer" if the
   cross-correlation peak is below a SNR threshold.
6. **UI:** wire as the third option in the bus chip popover next to
   the per-target sliders.

Implementation notes:

- The chirp + cross-correlate path is small (a few hundred lines of
  C# + an FFT). NAudio + `Cavern.Filters.FastConvolver` already in
  the project.
- Run on a background thread. Block the bus engine only during the
  measurement (≈ 2 seconds total).
- Auto-mute / unmute targets via WASAPI session volume so we don't
  bring the mixer down.
- Save a per-device "calibrated offset + timestamp" to
  `UserSettings`; show a small "Auto-synced 3 days ago" tag on the
  card.

### L3.b — Drift watchdog

- Re-run auto-sync when the bus engine sees a significant format
  change or a device is hot-plugged.
- Optional periodic re-check (every N hours, off by default).

### L4 — Predictive defaults — **partially shipped**

**Code:** `Core/Latency/PredictiveLatencyDefaults.cs`, `LatencySeedResolver.cs`, wired from `DeviceManager.GetRenderDevices`.

- **Name-based table** (substring match, first hit wins): common headphones, TVs, and cast-like devices get a better first Sync seed than the coarse L1 class alone. Values are conservative and clamped `0 … 120` ms.
- **User `LatencyClassDefaults` map** (from `UserSettings`) is applied when seeding a new endpoint id, after L4 name match and before the hard-coded `DeviceClassInfo` baseline.
- **Tools page UI** — five sliders (**On-board**, **USB**, **HDMI**, **Bluetooth**, **Network**) + **Reset to built-in class values** edit `LatencyClassDefaults` without touching JSON.
- **Future:** median offsets from anonymised calibration telemetry, or a downloadable JSON table.

## Risk register

| Risk | Mitigation |
|---|---|
| Mic auto-sync can pick up the room as well as the speaker chirp | Use FFT-based cross-correlation, not peak detection; reject low-SNR results. |
| BT codec switches mid-stream change latency by ±50 ms | Drift watchdog re-runs auto-sync on format change. |
| Asking for mic access spooks the user | Auto-sync is opt-in; UI explains why we need the mic and that no audio is uploaded. |
| Limited time budget on system shutdown | `SystemEvents.SessionEnding` handler caps disengage at 500 ms; fall back to "leave default as bus" on overrun (next launch fixes it). |

## Acceptance criteria for v1 (L1 only)

- All real outputs the user toggles on play in audible sync after
  ten seconds, without touching a slider, on a representative laptop
  with one USB DAC + one BT headphone + one HDMI TV.
- The bus chip popover always shows a total-latency number. The
  number changes when the user moves any slider.
- Class chip is correct for each detected device (manual review
  during testing, no automated test yet).
