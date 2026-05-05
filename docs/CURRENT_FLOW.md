# SonicFlow / KhurramAudioRoute — Current Architecture & Status

> Snapshot generated 2026-05-04 from a full pass over the codebase via the
> `codebase-memory` MCP graph (3962 nodes, 6015 edges) plus targeted reads of
> the hot files (`BassEngine`, `MasterEngine`, `PowerService`, `MainViewModel`,
> `DeviceManager`, `DuplicationManager`, `SpatialPipeline`, `UserSettings`,
> `MicChainEngine`).
>
> This is a **what's-actually-in-the-code** doc. Where behaviour does not match
> the user's mental model, that is called out explicitly under *Gaps & open
> questions*. The two earlier plan docs (`AUDIO_BUS_PLAN.md`,
> `MIC_CHAIN_PLAN.md`) describe **intent**; this one describes **state**.

---

## 1. The 10-second mental model

```
   Real apps (Spotify, Teams, browser, ...)
                  │  (default device → VB-CABLE)
                  ▼
   ┌────────────────────────────────────────┐
   │  VB-CABLE Output (loopback capture)    │
   └────────────────────────────────────────┘
                  │ WASAPI loopback
                  ▼
   ┌────────────────────────────────────────┐
   │     BassEngine "bridge"                 │
   │   push stream → master mixer → splits   │
   │       master EQ + spatial + limiter     │
   └────────────────────────────────────────┘
                  │  fan-out
        ┌─────────┼─────────┬─────────┐
        ▼         ▼         ▼         ▼
   Headphones  Speakers  HDMI TV  Bluetooth
   (each opened as its own WASAPI shared-mode session)
```

**Two completely independent chains live in this app:**

| Chain | Engine | Source | Sink |
|---|---|---|---|
| **Playback / output** | `BassEngine` (BASS / BASSmix / BASSWASAPI) | VB-CABLE Output loopback | every Active hardware output |
| **Mic / voice studio** | `MicChainEngine` (NAudio + SoundTouch.Net) | real microphone WASAPI capture | CABLE-B Input → CABLE-B Output (apps' input) |

They share **nothing** — settings, code, UI, threads. Touching one cannot break the other.

---

## 2. Master Power flow (`Core/PowerService.cs`)

`PowerService` is a small state machine: `Disabled ↔ Engaging ↔ Active ↔ Disengaging ↔ Failed`. The Outputs page header power chip and the tray-menu Power item both call into it.

### Engage (`EngageAsync`)

1. Lock `_stateGate` (re-entrancy guard); single Engage at a time.
2. If `BusDevice` (VB-CABLE Output endpoint) is missing → `IsBackupModeActive = true`, status: *"Install VB-CABLE for processed audio…"*. Power state still flips to Active.
3. Capture the **current Windows default playback device id**, persist it as `UserSettings.LastWindowsDefaultDeviceId` so we can restore it on Disengage.
4. Call `AudioRouterNative.SetSystemDefaultDevice(busId)` → flips Windows default to VB-CABLE.
5. Persist `WasPoweredOnAtClose = true` so a relaunch can re-engage automatically.
6. **Subscribers of `OnStateChanged`** then run — `MainViewModel.OnPowerStateChanged` rebuilds the bridge.

### Disengage (`DisengageAsync`)

1. Pull `LastWindowsDefaultDeviceId` from settings.
2. If it's set and isn't the bus, `SetSystemDefaultDevice(previous)` → restore.
3. `IsBackupModeActive = false`, state → `Disabled`.
4. Persist `WasPoweredOnAtClose = false`.
5. Subscribers see the state change and tear down the bridge (`StopBridge`).

### Auto-engage on launch

`MainViewModel` ctor calls `PowerService.AutoEngageIfPersisted()` if `UserSettings.GetWasPoweredOnAtClose()` is true. So if you exited with the bus on, the next launch comes up with the bus on.

### Force-shutdown handling (`DisengageOnShutdown`)

Hooked to `Application.SessionEnding`. Best-effort sync restore of the previous Windows default; capped at ~500 ms so Windows doesn't block waiting for us.

---

## 3. The BASS bridge (`Core/BassEngine.cs`, `Core/MasterEngine.cs`)

The bridge is one big static state machine inside `BassEngine`. Driven by `MainViewModel.RebuildMasterBridge`.

### When does `RebuildMasterBridge` fire?

- After Power.OnStateChanged → engage
- When the user toggles `IsActiveOutput` on a hardware card
- When `RefreshData` rediscovers devices
- After a sync wizard / auto-sync wizard completes
- After matrix-bridge / channel-order toggles in Tools

### Bridge build (`StartBridge` in `BassEngine`)

Pseudo-code of what happens each rebuild:

```
1. StopBridge()                         ← idempotent teardown of previous run
2. _bridgeMatrixChannelOrder = settings  ← snapshot UserSettings.MatrixBridgeChannelOrder
3. BassWasapi.Init(source, loopback)     ← VB-CABLE loopback capture session
4. captureFreq, captureChans = info      ← negotiated format (typically 48k/2)
5. _bridgePushStream = Bass.CreateStream(Decode | Float | Push)
   (loopback callback BridgeSourceCallback pushes captured frames into this)
6. _bridgeMasterMixer = BassMix.CreateMixerStream(48000, 2, Decode | Float | MixerNonStop)
7. BassMix.MixerAddChannel(_bridgeMasterMixer, _bridgePushStream, MixerChanDownMix | MixerNonStop)
8. _bridgeEqDsp = new MasterEqDsp(_bridgePushStream, gains)
   ← 10-band biquad EQ via Bass.ChannelSetDSP
9. AttachBusSoftLimiterFx(_bridgeMasterMixer)
   ← DX8 / BASS_FX compressor for safety against EQ clipping
10. for each target: split = Bass.Split.CreateSplitStream(_bridgeMasterMixer, …)
    matrix-mode? CreateMatrixBridgePullProc → 5.1 / 7.1 Hafler upmix
    BassWasapi.Init(target, push) on the target endpoint
    BassWasapi.Start()
11. Wait until source loopback starts → audio flows through
12. Apply persisted spatial preset to the source channel
```

### Key invariants

- `_bridgeMasterMixer` has flag `Decode | Float | MixerNonStop` — never auto-played. Splits pull data from it.
- The **EQ DSP is attached to the push stream**, not the master mixer (see *Gaps & open questions* below for why this changed).
- Each target gets its own WASAPI shared-mode session at the target's mix format. Sample-rate / channel count conversion happens in BASS via per-target convert streams.
- `UpdateBridgeTargetLatency(deviceId, ms)` updates BASS sample delay on a per-target split — this is what the per-output **Sync** slider drives.

### Bridge teardown (`StopBridge`)

Reverse of build: stop each WASAPI target, free splits/converts, dispose `MasterEqDsp`, free master mixer, free push stream, free WASAPI source. State maps emptied.

---

## 4. Spatial pipeline (`Core/Spatial/SpatialPipeline.cs`)

Independent layer that runs **on the bridge source channel** via `BassEngine.SetSpatialPreset(deviceId, preset)`. Each preset resolves to an `ISpatialStage[]` chain via `SpatialPipelineFactory.Create(preset, masterStereoWidth)`.

| Preset | Chain (top → bottom) |
|---|---|
| Off | passthrough |
| Headphone Stereo+ | Master width → CrossfeedStage → SoftLimiter |
| Headphone Studio | Master width → CrossfeedStage → EarlyReflectionRoom (small, dry) → SoftLimiter |
| Headphone Cinema | Master width → VirtualSurround → EarlyReflectionRoom (medium) → SoftLimiter |
| Headphone Concert hall | Master width → VirtualSurround → EarlyReflectionRoom (large) → SoftLimiter |
| Speakers 5.1 | Master width → MatrixUpmix(5.1) → SoftLimiter |
| Speakers 7.1 | Master width → MatrixUpmix(7.1) → SoftLimiter |
| Game | Master width → CrossfeedStage (tighter) → SoftLimiter |

**Master stereo-width slider** prepends a `StereoWidthStage` to every non-Off preset (master mid/side scaling).

The spatial pipeline is run by an internal BASS DSP loop on the source channel. **Same possible audibility issue as the EQ if the DSP doesn't reach the splits**; the user has reported the spatial pills also don't audibly change anything, which is consistent with this hypothesis.

---

## 5. Per-output state and the **ACTIVE** chip

`AudioDevice.IsActiveOutput` is a per-device boolean stored on the in-memory device list. Toggling it:

1. Triggers `OnPhysicalActiveToggled(device)` in `MainViewModel`.
2. Calls `RebuildMasterBridge()` → BASS bridge fans out to whatever is currently Active.

**ACTIVE today = "this output is part of the live bridge mix."** Untick → no audio reaches that output. Tick → audio + master EQ + master spatial + per-output sync offset reach that output.

It is **not** a group / preset selector. Multiple devices Active at once IS the group (every Active device gets the same processed audio). But the word "ACTIVE" doesn't convey grouping clearly.

The **per-output Sync slider** lives next to ACTIVE; it pushes a sample delay into the bridge target (via `BassEngine.UpdateBridgeTargetLatency`) so different physical outputs stay phase-aligned.

---

## 6. Settings persistence (`Core/UserSettings.cs`)

JSON file at `%APPDATA%\SonicFlow\settings.json` (LocalApplicationData fallback). Tiny model. Re-read on every Get; locked write on every Set.

What's currently persisted:

| Key | What |
|---|---|
| `WasPoweredOnAtClose` | auto-engage on next launch |
| `LastWindowsDefaultDeviceId` | restore on Disengage |
| `MasterEqualizerGains` (10 bands) | master EQ |
| `MasterSpatialPreset` | master spatial |
| `MasterStereoWidth` | master width slider |
| `LatencyClassDefaults` | L4 per-class default Sync (BT / HDMI / USB / OnBoard) |
| `TargetLatencyOffsets[deviceId]` | per-output Sync slider value |
| `AdvancedExpandedByDeviceId` | Outputs Advanced section per-card collapsed/expanded |
| `L2SyncCalibrations[deviceId]` | L2 perceptual sync per output |
| `L3AutoSyncCalibrations[deviceId]` | L3 mic-based auto-sync per output |
| `SuppressVbCableStartupReminder` | first-run modal don't-show-again |
| `MatrixSurroundBridgeUpmix` | Tools matrix toggle |
| `MatrixBridgeChannelOrder` | Auto / Internal / WindowsHdmi7_1 |
| `SpatialPresets[deviceId]` | per-device spatial (legacy single-device path) |
| `EqualizerGains[deviceId]` | per-device EQ (legacy single-device path) |
| Mic chain: `MicChain*` keys | mic id, render id, gate, voice preset, studio polish, reverb, denoise, pitch |

What's **NOT** persisted today (gap):

- **Which outputs are ACTIVE** (`IsActiveOutput`) — recomputed each launch from device defaults, so the user's group dissolves between sessions.

---

## 7. Per-app duplication (`Core/DuplicationManager.cs`)

Independent of the bridge. Used only when SonicFlow Virtual is in play (today: a hidden plumbing path for the Apps page) and for legacy per-app "mirror to" behaviour.

NAudio session-level `WasapiLoopbackCapture` per source → in-process EQ + spatial DSP → fan-out to selected target devices. `UpdateEqualizerMirrorSessions` and `UpdateSpatialMirrorSessions` push the master EQ / spatial state into every active duplication session so the per-app path stays in sync with master.

This is **not** the path you hear on the main Outputs flow today. It's used when an app is explicitly routed via the Applications page.

---

## 8. UI surfaces

- **Outputs** *(default tab)* — power chip, master EQ + presets, master spatial pills + width, master stereo width, per-device cards (ACTIVE chip, Sync slider, RE-CAL chip, DRIFT chip, Auto-synced caption), Sync wizard button, Auto-sync wizard button, total-latency banner.
- **Applications** — per-app sliders, mirror-to lists.
- **Mic Devices** — Voice Studio card (full mic chain), capture-device list.
- **Other Options / Tools** — Default Sync by class sliders, matrix-surround upmix toggle, channel-order combo, diagnostics buttons (Run Core Tests, Diagnose Audio).
- **Sync wizard** (modal) — three perceptual taps per Active output for L2 calibration.
- **Auto-sync wizard** (modal) — mic-based chirp + cross-correlation for L3 calibration.

---

## 9. Mic chain status (Voice Studio card)

Already feature-complete on the v2 plan (Phases 1–10):

- NoiseGate → VoiceEq → DeEsser → Compressor → PitchShifter → VoiceReverb → SoftLimiter → PeakMonitor → resample → stereo → render
- Unified **Voice preset** pill row (10 pills, Off + 4 EQ flavours + 5 character macros)
- **Studio polish** pill row (Off / Soft / Strong / Broadcast)
- **Reverb** pill row (Off / Booth / Plate / Room / Hall / Cathedral)
- **Noise reduction** pill row (Off / Light / Medium / Strong)
- Gate threshold + hold + Reset, pitch slider, input + output meters, status line
- Persistence under `MicChain*` keys

See `docs/MIC_CHAIN_PLAN.md` for full details.

---

## 10. What works today (verified by code review + recent QA logs)

- ✅ Master power Engage / Disengage with Windows-default flip + restore.
- ✅ Auto-engage on launch when bus was on at last close.
- ✅ Bridge starts with N targets at the negotiated mixer format (48k/2).
- ✅ Per-target WASAPI session opens at each target's mix format with format conversion.
- ✅ Per-output Sync slider (BASS sample delay) — no QA confirmation but logs show `UpdateBridgeTargetLatency` calls.
- ✅ L1 latency baseline + L2 perceptual sync wizard + L3 mic-based auto-sync (code-complete; pending hardware QA).
- ✅ First-run VB-CABLE reminder modal + don't-show-again.
- ✅ Backup mode (loopback off Windows default when no VB-CABLE).
- ✅ RefreshBus on hot-plug of VB-CABLE.
- ✅ Mic chain v2 (entire Voice Studio).
- ✅ Settings persistence for everything in section 6.

---

## 11. Gaps & open questions *(things the user has flagged)*

### a) Master EQ slider doesn't audibly change output

**Status:** investigating, two-fix attempts shipped.

- **Original:** EQ FX attached to `_bridgeMasterMixer` via `Bass.ChannelSetFX(EffectType.PeakEQ)`. Failed with `Type` error → fell back to DX8 ParamEQ. **DX8 effects silently no-op on `BassFlags.Decode` streams** even though the FX attaches and `FXSetParameters` succeeds.
- **Fix v1:** replaced FX with `MasterEqDsp` (10-band NAudio biquad chain via `Bass.ChannelSetDSP`) attached to the master mixer. Still inaudible → BASSmix splits read the mixer's internal pre-DSP buffer, so DSP attached to a mixer never reaches splits.
- **Fix v2 (current):** `MasterEqDsp` now attaches to `_bridgePushStream` instead of the mixer. The push stream is a real decode channel that the mixer always pulls from, so DSP on it always runs. **User reports still inaudible** as of last test — needs another diagnostic pass.
- Likely next step: attach a `MasterEqDsp` to **each per-target split** (guaranteed pull by WASAPI) instead of the source. Costs N attachments instead of 1 but guarantees the path.

### b) Master spatial pills don't audibly change output

Probably the same root cause as (a): the spatial pipeline runs through a BASS DSP on the bridge source. Same uncertainty about whether mixer-side DSPs reach splits. Once the EQ is fixed and we know the right attachment point, spatial should follow the same fix.

### c) ACTIVE state is not persisted across restarts

`IsActiveOutput` is in-memory only. On launch, the group needs to be rebuilt manually. **Action:** add `UserSettings.ActiveBridgeTargetIds` (List<string>) and apply on `RefreshData`.

### d) Power toggle doesn't restore the previously-playing device

User report: "on switch on and off it's not playing what last it was playing." `PowerService` does persist `LastWindowsDefaultDeviceId` and restore on Disengage, but the **Active group** itself isn't persisted (see c). So when you re-engage, the bridge fans out to whatever defaults `IsActiveOutput` got from a fresh `RefreshData` — usually a smaller set than what was previously selected.

### e) Switch-on / switch-off feels slow

User report: "switching devices is now slow." Possible causes:

- BASS WASAPI sessions take ~50–150 ms to start each on a cold device; if 4+ devices are in the group, that's 200–600 ms cumulative.
- VB-CABLE Default-flip via PolicyConfig API can stall briefly.
- `RefreshData` enumerates every endpoint and updates the in-memory device list — heavy on first run after a hot-plug.
- We tear down + rebuild the entire bridge on every ACTIVE toggle. Could be incremental (add/remove single target) instead of full rebuild.

### f) "ACTIVE" naming confusion + missing group concept

ACTIVE today = "include in live mix." User wants a separate **Group / Mirror** concept where multiple outputs can be saved as named groups (e.g., "Living room" = Speakers + HDMI + Subwoofer). Not implemented.

### g) BASS "Busy" target init failure

One device fails to open every bridge restart with `Bass.LastError = Busy`. That's a real device conflict — something else has it open exclusively. Not a bug we caused, but the UX could surface a warning chip on that card so the user knows why it's not getting audio.

---

## 12. Suggested next-priority order

1. **Fix master EQ audibility** — try per-split DSP attach (the only attachment point we haven't verified). 30 min change.
2. **Fix master spatial audibility** — same fix path as (1). Already-shipped pipeline; just needs the right attachment point.
3. **Persist ACTIVE group across restarts** — `UserSettings.ActiveBridgeTargetIds` + apply on launch.
4. **Rename ACTIVE chip** to something the user prefers (`In group`, `Live mirror`, `Share`, etc.) once the persistence ships.
5. **Group/Mirror page** — named groups stored per name, recall via dropdown.
6. **Faster bridge restart** — incremental target add/remove instead of full rebuild on each toggle.
7. **Surface Busy errors on per-device cards** — small warning chip explaining "another app has this device open."

Items 1–4 are short and unblock the user's core flow. 5 is a UX feature, 6 is performance polish, 7 is hygiene.
