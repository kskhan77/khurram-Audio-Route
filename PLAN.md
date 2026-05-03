# SonicFlow / KhurramAudioRoute — Project Plan

## Production direction (May 2026)

**VB-CABLE is the production audio bus.** SonicFlow's own Windows
driver work is **deferred indefinitely**: Microsoft no longer signs
hobbyist driver packages, and asking end users to disable Secure Boot
or BitLocker is a non-starter. VB-Audio's pre-signed virtual cable
already runs on every locked-down dev box we've tested, so we will
ship against it as a first-class dependency.

The driver-side documents in `Drivers/SonicFlowVirtualAudio/PLAN.md`
and `Drivers/SonicFlowVirtualAudio/SPATIAL_PLAN.md` remain in the repo
for future reference but are flagged **DEFERRED** at the top.

The detailed v1 architecture lives in three documents:

- [`docs/AUDIO_BUS_PLAN.md`](docs/AUDIO_BUS_PLAN.md) — the always-on
  bridge, the master power button, hidden virtual-profile plumbing.
- [`docs/LATENCY_PLAN.md`](docs/LATENCY_PLAN.md) — the L1 → L4 staged
  latency roadmap, ending with mic-based auto-sync.
- [`docs/MIC_CHAIN_PLAN.md`](docs/MIC_CHAIN_PLAN.md) — the future
  CABLE-B "voice studio" capture chain (post-v1, but in the plan).

## Scope of v1

v1 ships **playback / Outputs** as the hero path. Applications and Mic
pages stay in the shell for later milestones; feature plans live under
`docs/`.

---

### v1 — DONE (earlier milestones)

- [x] WPF dashboard shell with dark styling; left navigation
  (`Outputs`, `Applications`, `Mic Devices`, `Other Options`).
- [x] `Outputs` as default section on startup.
- [x] Live app-level meters + per-app volume sliders (Applications page).
- [x] Microphone discovery page.
- [x] Compact playback cards + explicit **Set Default** affordance.
- [x] Responsive startup sizing (~60% screen height).
- [x] Fixed XAML parse crash on `Equalizer24`.

---

### v1 — DONE (`docs/AUDIO_BUS_PLAN.md` master bus — implemented)

- [x] **Master power** (header + tray): engage / disengage, persist
  previous Windows default (`UserSettings`), restore on shutdown / tray exit.
- [x] **Always-on BASS bridge** while powered: capture from VB-CABLE (or backup
  loopback tap) → `MasterEngine` EQ + spatial + limiter → fan-out to
  hardware marked **ACTIVE** (`MainViewModel.RebuildMasterBridge`, `Core/MasterEngine.cs`).
- [x] **Backup mode** when no virtual bus: loopback on current multimedia default;
  Outputs banner when VB-CABLE missing + VB-Audio hyperlink (`PowerService`,
  `MainWindow.xaml`).
- [x] **Auto-engage** on launch when `WasPoweredOnAtClose` — including backup
  (no gate on `BusDevice`).
- [x] **Hide Profile 1/2 labels** in UI; virtual profile cards collapsed / not
  emphasized on Outputs.
- [x] **Master EQ + Master Spatial** card at top of Outputs; per-hardware
  mirror + profile EQ/spatial removed from shipped Outputs surface.
- [x] **ACTIVE** chip per real output; sync sliders + persisted offsets.
- [x] **L1 latency** baseline: device-class chips + defaults + total-latency banner
  (see [`docs/LATENCY_PLAN.md`](docs/LATENCY_PLAN.md) L1).
- [x] **L2 sync wizard**: Outputs **Sync wizard** launches `SyncCalibrationWindow`
  (three perceptual taps per ACTIVE output ±20 ms, WASAPI click bursts via
  `Core/SyncCalibration`, persists `UtcTicks` + mix fingerprint to `UserSettings` — [`docs/LATENCY_PLAN.md`](docs/LATENCY_PLAN.md) L2).
- [x] **DuplicationManager** narrowed: Outputs hardware mirroring goes through the
  bridge only; duplication kept for SonicFlow virtual profile plumbing + Applications
  per-session mirroring (`ToggleDeviceDuplicate` / `ApplyDeviceDuplicateTargets`
  gated on `IsSonicFlowVirtual`).
- [x] **Legacy preset clicks** (hidden template) routed to master EQ/spatial VM.
- [x] **Persist `IsAdvancedExpanded`** per device id across restarts (`UserSettings.AdvancedExpandedByDeviceId`).

---

### v1 — NEXT

- [x] **L2 polish** — **RE-CAL?** chip on hardware cards when persisted L2 fingerprint
  differs from a one-shot WASAPI probe (`AudioDevice.SyncCalibrationStale`,
  `L2CalibrationStaleHints`, refreshed each `RefreshData` + after Sync wizard closes).
- [x] **First-run VB-CABLE reminder** — modal on first idle after load when the bus device
  is missing; VB-Audio link; optional **Don't show again** persists in `UserSettings`
  (`SuppressVbCableStartupReminder`). In-app banner unchanged.
- [ ] **Regression pass** — run the *Testing checklist* section below on a VM or disposable Windows profile; sign off items or file issues.

---

### Running the regression pass (v1 closure)

Use a profile without stale `%APPDATA%\SonicFlow\settings.json` when possible **or** back up/delete that folder first so VB modal + defaults behave like first run.

**0. Automated preflight** (optional — clean build under `%TEMP%`, avoids IDE file locks):

`powershell -ExecutionPolicy Bypass -File .\Scripts\RegressionPass.ps1`  
(or `-Configuration Debug`; use `pwsh` if PowerShell 7 is installed.)

1. **VB-CABLE present** — work through checklist rows that assume bus installed (power toggle, persist, ACTIVE outputs, wizard, sliders).
2. **VB-CABLE absent** — confirm Outputs banner + first-run modal (until suppressed), backup loopback behaviour, **`RefreshBus` after simulated install**: exit app tray, reinstall cable, reopen app.
3. **L2 UX** — after a wizard run with saved fingerprint, change Windows sample rate exclusive vs shared scenario if you can provoke drift; **`RE-CAL?`** chip should appear and open **Sync wizard**; after re-run chip clears once fingerprint matches again.

---

### v2 (after v1 ships)

- **L3 latency** — mic-based auto-sync (chirp + cross-correlate).
- **Sound upscaling** — `MatrixUpmixStage` + stereo-width master toggle.
- **Applications page** — per-app routing onto the bus + optional EQ overrides (`PLAN.md` scope creep).
- **Microphones page** — [`docs/MIC_CHAIN_PLAN.md`](docs/MIC_CHAIN_PLAN.md).
- **Continuous drift watchdog** after format changes.
- **Soundboard / utility tools** if still wanted.

### Deferred (future)

- SonicFlow Virtual Speaker driver (old Phase 2–5). Deferred unless signing rules change.
- ML stem separation, personalised HRTFs, Atmos / object decode.
- Power-user dual-bus (e.g. CABLE A vs B).

---

## Testing checklist *(manual QA)*

- [ ] Toggle master power — default flips to VB-CABLE while ON, restores when OFF.
- [ ] Restart app while powered ON — bus engages; default is VB-CABLE when cable present.
- [ ] Force-close while powered ON — previous default restored on next launch/login.
- [ ] Master EQ + spatial audible on every **ACTIVE** real output.
- [ ] Untick **ACTIVE** on one device — others keep playing processed audio.
- [ ] Sync sliders + total-latency banner update live.
- [ ] **Sync wizard** (L2) — need ≥2 ACTIVE hardware sinks; perceptual tweaks move Sync sliders and persist JSON.
- [ ] **L2 RE-CAL?** chip — visible when persisted fingerprint mismatches live probe; opens wizard; clears after refresh or matching re-calibration.
- [ ] First-run **VB-CABLE** modal shows when cable missing and reminder not suppressed; **Don't show again** persists; **`SuppressVbCableStartupReminder`** survives restart.
- [ ] Uninstall VB-CABLE — backup banner + loopback behaviour; reinstall hot-plugs via `RefreshBus`.
- [ ] **Other Options -> Diagnostics**: three Audio Core Integrity checks log to Debug Output (equalizer silence, gain bump, enumeration); confirm no FAILED lines.
