# Audio Bus Architecture (v1)

This document describes how the **playback-side** master bus works
once we power the app on. Application-side per-app routing and the
mic studio chain have their own plans
([`MIC_CHAIN_PLAN.md`](MIC_CHAIN_PLAN.md)) and are explicitly not in
v1 scope.

## Mental model

```text
                          ┌─ POWER ●  (header + tray) ─────────────────────┐
                          │                                                │
   Windows app audio  ───►│  CABLE Input (auto-default while POWER=ON)     │
                          └────────────┬───────────────────────────────────┘
                                       │ WASAPI loopback
                                       ▼
                       ┌──────────────────────────────┐
                       │  MASTER ENGINE (always-on    │
                       │  while POWER = ON):          │
                       │   ▸ Master 10-band EQ        │
                       │   ▸ Master Spatial preset    │
                       │   ▸ Soft limiter             │
                       └────────────┬─────────────────┘
                                    │ fan-out via per-target splits
                  ┌─────────────────┼──────────────────┐
                  ▼                 ▼                  ▼
              Speakers          Headphones          Soundbar
               [● Active]        [● Active]          [○ Off]
               +0 ms             +30 ms (BT)         +12 ms (HDMI)
```

The user only ever sees:

- **Header strip** — `[POWER ●]  Master EQ  +  Master Spatial  +  bus
  health chip`.
- **Real outputs list** — each device card has an `[●] Active`
  chip + sync slider. No virtual cards.

The "Profile 1 / 2" labels currently produced by `MainViewModel`
become an **internal-only** detail: the engine still picks one
VB-CABLE endpoint as the bus, but the UI doesn't expose it.

## Master power-button contract

State machine:

```text
Disabled ─► Engaging ─► Active ─► Disengaging ─► Disabled
   ▲                                              │
   └──────────── Failure / restart ◄──────────────┘
```

### Engage (`POWER OFF → ON`)

1. Resolve a virtual bus endpoint via
   `SonicFlowVirtualAudio.FindVirtualRenderDevice` (matches VB-CABLE
   A / B / C / D, plus SysVAD as a development hook).
2. If no virtual endpoint is found → fall back to **self-mirror**
   mode (see "Backup mode" below) and stop here.
3. Persist the **outgoing** Windows default device id into
   `UserSettings.LastWindowsDefaultDeviceId` (only if it isn't the
   bus itself).
4. Call `AudioRouterNative.SetSystemDefaultDevice(busDeviceId)` so
   Windows routes apps into the bus.
5. Start `BassEngine.StartBridge` with the bus as source. Loopback
   pulls into the master pipeline (EQ + spatial + limiter), then
   splits into the user's `[● Active]` real outputs.
6. Apply per-target latency offsets (see
   [`LATENCY_PLAN.md`](LATENCY_PLAN.md)).
7. Mark `PowerService.IsActive = true`. Tray + header chip light up
   green.

### Disengage (`POWER ON → OFF`)

1. Stop the bridge (`BassEngine.StopBridge`) cleanly. WASAPI handles
   are released; outputs go silent.
2. Restore the previous Windows default from
   `UserSettings.LastWindowsDefaultDeviceId` if that endpoint is
   still active.
3. Clear `LastWindowsDefaultDeviceId` only if the restore
   succeeded — otherwise we keep retrying on next engage.
4. Set `PowerService.IsActive = false`.

### Restart / shutdown

- On `MainWindow.OnClosing` (via tray exit) — disengage first.
- On `SystemEvents.SessionEnding` — disengage; do NOT block the
  shutdown for more than 500 ms.
- `UserSettings.WasPoweredOnAtClose` records the last user-visible
  state. On the **next** launch, if it was on, re-engage silently
  (best-effort, no popups).

### Failure modes

| Scenario | Behaviour |
|---|---|
| VB-CABLE installed but rejected `Init` | Surface yellow chip + retry once after 2 s; then fall back to self-mirror. |
| Windows refuses `SetSystemDefaultDevice` | Log; surface chip warning; bridge does not start. |
| VB-CABLE uninstalled mid-session | Bridge auto-disengages; banner offers re-install. |
| Bus endpoint format change (44.1 ↔ 48 kHz) | Bridge tears down and re-engages without restoring default (it is already the bus). |

## Master engine

A new lightweight `MasterEngine` class centralises the DSP that used
to be per-device:

- `MasterEqGains[10]` — fed straight into BASS PeakEQ FX on the
  master mixer (`BassEngine.StartBridge` already creates a master
  mixer, so the EQ FX live there).
- `MasterSpatialPreset` — drives the existing
  `Core/Spatial/SpatialPipeline.cs`. The pipeline runs **once** on
  the captured stream before fan-out, so adding outputs doesn't
  multiply CPU.
- `SoftLimiterStage` is always last.

`AudioDevice.EqBand0..9` and `AudioDevice.SpatialPreset` survive on
the model for the time being but are no longer bound from the UI.
They are scheduled for removal once the v1 master flow is stable.

## UI changes

### Removed / hidden

- Profile labels ("Profile 1", "Profile 2"). The cards for virtual
  endpoints are removed entirely from the Outputs page.
- The per-device `Mirror outputs` toggle inside the advanced section.
- The per-device EQ + spatial controls inside the advanced section.

### Added

- **Header power chip** (top-right of the Outputs page + tray menu):
  click to engage / disengage. Hover shows total perceived latency
  (see [`LATENCY_PLAN.md`](LATENCY_PLAN.md)).
- **Master EQ + Spatial card** at the top of the Outputs page.
  Reuses the styles we already shipped (`SonicEqPresetRadioCompact`,
  `SonicSpatialPresetListRadio`).
- **`[● Active]` chip** on every real output card. Toggling adds or
  removes that target from the bridge fan-out without restart.

### Surfaces that stay the same

- The Applications page (per-app meters + sliders) is untouched in
  v1.
- The Microphones page is untouched in v1.

## Backup mode (no VB-CABLE)

Trigger conditions:

- `SonicFlowVirtualAudio.FindVirtualRenderDevice` returns null.
- VB-CABLE was uninstalled mid-session.

Behaviour:

- Show a top banner: **"Install VB-CABLE for processed audio on every
  output."** Link to the VB-Audio download page.
- Power button still functions but uses the current Windows default
  as the **source of capture** (WASAPI loopback on it).
- Real outputs that the user marks `[● Active]` receive the
  processed copy. The default device itself stays dry — banner spell
  this out.
- All other behaviour (master EQ, master spatial, latency offsets)
  unchanged.

## File / class map

| Area | Today | After v1 |
|---|---|---|
| Power state | not centralised | `Core/PowerService.cs` (new) |
| Last-default persistence | n/a | `Core/UserSettings.cs` (extend) |
| Bridge (BASS) | `Core/BassEngine.cs::StartBridge` | unchanged engine; add master EQ + spatial slots |
| Spatial DSP | `Core/Spatial/SpatialPipeline.cs` | unchanged; runs once on the bus |
| Mirror DSP | `Core/DuplicationManager.cs` | **SonicFlow virtual profile rows + Applications duplication** only; Outputs hardware mirrors use **`RebuildMasterBridge`**, not `ToggleDeviceDuplicate` on physical endpoints |
| ViewModel | `ViewModels/MainViewModel.cs` | gains `PowerService` binding, drops Profile labelling |
| MainWindow | `MainWindow.xaml` | header power chip + master EQ/spatial card; per-virtual-device cards removed |

## Out of scope for v1 (and where they live)

- Per-app routing — Applications page, future plan.
- Mic voice-studio chain — [`MIC_CHAIN_PLAN.md`](MIC_CHAIN_PLAN.md).
- Sound upscaling toggle (Stereo → 5.1 / 7.1) — handled in v2 via the
  existing `MatrixUpmixStage`.
- Personalised HRTFs, ML denoise, Atmos decode — deferred.
