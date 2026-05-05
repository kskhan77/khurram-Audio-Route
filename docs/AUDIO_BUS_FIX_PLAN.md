# Audio Bus — Routing & UX Fix Plan

> **Why this doc exists.** EQ debugging keeps stalling because the
> underlying audio routing is broken from the user's POV. Until the bus
> reliably puts processed audio in the user's ears, no DSP test (EQ, spatial,
> kill switch) tells us anything useful. This plan captures what's broken,
> what the *intended* flow is, and the fixes needed in priority order.
> Companion to `docs/CURRENT_FLOW.md` (which describes existing state).

---

## 1. Current broken behaviour *(reported)*

| # | Symptom | Why it happens |
|---|---|---|
| **B1** | App auto-engages bus on launch → music currently playing **stops** | `EngageAsync` flips Windows default to VB-CABLE; running apps cache their target device on stream start and don't follow the change |
| **B2** | After bus is on, user clicks **Set Default** on a hardware device to "make audio come back" — this **bypasses the bridge entirely** | Set Default flips Windows default away from VB-CABLE; apps play direct to hardware; bridge captures silence; EQ/spatial do nothing |
| **B3** | Even when audio *should* route through the bridge (VB-CABLE is default, app refreshed), user doesn't hear anything from ACTIVE devices | Either the bridge fan-out is silently broken on this hardware, or the original default device wasn't auto-marked ACTIVE so audio fans to a device the user isn't listening to |
| **B4** | EQ slider moves but no audible change | Combination of B2 + B3 — the audio the user hears doesn't pass through the bridge, so DSP can't affect it |
| **B5** | ACTIVE state used to reset to all-on every refresh (FIXED) | Persistence shipped — `UserSettings.ActiveBridgeTargetIds` now sticks |
| **B6** | When bus engages with auto-pick-default logic, the device that was Windows default *before* the engage should become ACTIVE; instead, my new logic queries default *after* the flip and gets VB-CABLE (which it correctly skips), leaving zero ACTIVE devices | `ConfigureDeviceDuplicateTargets` reads the current default at the wrong moment |
| **B7** | "Set Default" button on hardware cards is always enabled, even while bus is on | No UX guard against the bypass footgun |

---

## 2. Intended flow *(what the user expects)*

### Bus **OFF** (idle):

- Windows default = whatever the user picked (Speakers / headphones / HDMI).
- Apps play direct to that default device. **SonicFlow does nothing.** No EQ, no spatial, no overhead.
- The Outputs page shows hardware devices but no DSP is applied.

### Bus **ON** (engaged):

- Windows default = **VB-CABLE Input** (auto-set by SonicFlow on engage).
- Apps play to VB-CABLE → loopback to VB-CABLE Output.
- SonicFlow's bridge:
  1. Captures VB-CABLE Output (loopback)
  2. Runs master EQ + spatial + soft limiter
  3. Fans out the processed audio to every device the user marked **ACTIVE** (in their group)
- User hears the processed audio from the **ACTIVE** device(s) — same physical speakers/headphones they were listening to before the bus engaged.

### Switching devices:

- When bus is **OFF**, "Set Default" works normally (Windows-level default).
- When bus is **ON**, the user **does not** use "Set Default" on hardware cards. Instead they use **ACTIVE chips**: tick a hardware card → it joins the live mix; untick → it drops out. "Default" stays on VB-CABLE the whole time.

### App-level routing:

- Apps that follow Windows default device changes (browsers, Windows test sound, most modern apps) → automatically route to VB-CABLE → bridge → speakers when bus engages.
- Apps that cache the device on stream start (Spotify, some media players) → may need a stream restart (refresh / pause+play) after bus engages. **This is unavoidable** — we can't force foreign apps to switch device mid-stream.
- We will **inform the user** that they may need to restart audio when the bus engages, instead of leaving them confused.

---

## 3. Fix order *(smallest → largest, ship in this order)*

### Fix 1 — Auto-engage on launch should not break existing audio

**Change:** stop calling `AutoEngageIfPersisted()` automatically. Instead:

- On launch, if `WasPoweredOnAtClose=true`, show a small banner / toast on the Outputs page: *"Last session ended with the audio bus on. Click here to engage."* Don't auto-engage.
- User can also re-enable auto-engage in settings if they really want it (off by default).

**Rationale:** the auto-engage was supposed to be a convenience; in practice it breaks whatever's playing pre-launch. Better to have the user explicitly trigger engage so they know audio is about to be re-routed.

**Files:** `MainViewModel.cs` (constructor), `Core/PowerService.cs:AutoEngageIfPersisted`, optional new banner in `MainWindow.xaml`.

### Fix 2 — On engage, auto-mark the previous Windows default as ACTIVE

**Change:** when `EngageAsync` runs, BEFORE it flips Windows default to VB-CABLE, capture the previous default's device id (we already do this for `LastWindowsDefaultDeviceId`). After the bus engages and `RebuildMasterBridge` runs, ensure that previous-default device is marked ACTIVE if no other devices are.

**Rationale:** the device the user was just listening to should automatically receive the processed bridge audio so they don't perceive an audio drop.

**Files:** `Core/PowerService.cs:EngageAsync`, `MainViewModel.cs:RebuildMasterBridge` or `ConfigureDeviceDuplicateTargets`.

### Fix 3 — Disable "Set Default" on hardware cards while bus is on

**Change:** the **Set Default** button on hardware output cards becomes disabled (greyed) while `Power.IsActive == true`. Tooltip explains: *"Bus is engaged. Tick ACTIVE on this card to send processed audio here. Disengage the bus first to use this device directly without processing."*

**Rationale:** clicking Set Default on hardware while bus is on is a footgun that bypasses the bridge. Removing the option in this state forces the user into the correct mental model.

**Files:** `MainWindow.xaml` (Outputs cards), maybe a converter `BoolToInverseEnabledConverter`.

### Fix 4 — Show the audio routing path on the Outputs page

**Change:** add a single-line indicator at the top of the Outputs page that always shows where audio is going:

- Bus OFF: *"Audio plays direct to: [Windows default device name]"*
- Bus ON, no ACTIVE devices: *"Bus is engaged but no devices are receiving audio. Tick ACTIVE on a card."*
- Bus ON, N ACTIVE devices: *"Audio: apps → VB-CABLE → SonicFlow → [list ACTIVE device names]"*

**Rationale:** users currently can't tell where audio is supposed to go. This makes the routing visible.

**Files:** `MainWindow.xaml` (Outputs banner section), `MainViewModel.cs` (compute string).

### Fix 5 — "Restart audio" toast when bus engages

**Change:** when the bus engages, fire a non-modal toast on the Outputs page: *"Bus engaged. If your music stopped, restart it (refresh browser tab, pause+play in Spotify) so it routes through the bus."* Auto-dismisses after 8 s. Add a "Don't show again" link that persists in `UserSettings`.

**Rationale:** users currently think the app broke when the bus engages and audio stops. A clear message removes the confusion.

**Files:** `MainViewModel.cs`, simple toast control in `MainWindow.xaml`.

### Fix 6 — Verify the bridge fan-out is actually audible

**Change:** add a "Test bus output" button on the Outputs page. Clicking it generates a brief 1 kHz tone in the bridge's master mixer (in our code, before the splits) and plays it through every ACTIVE device. User hears a beep from each ACTIVE device — confirms the entire bridge → DSP → fan-out → hardware path works.

**Rationale:** this is the audible-verification step we keep needing during EQ debugging. Without it, "EQ doesn't work" is ambiguous between "EQ math wrong" and "no audio reaching speakers via bridge at all."

**Files:** `Core/BassEngine.cs` (new `PlayBridgeTestTone()` that injects a tone via `Bass.CreateStream`), Outputs button.

### Fix 7 — Rename "ACTIVE" chip to something clearer

**Change:** rename the chip label from **ACTIVE** to **In group** (or **Listen here** / **Receive** — pick one).

**Rationale:** "ACTIVE" reads like "this device is currently making sound" which is misleading. The chip really means "include this device in the bridge fan-out group." Clearer name = better mental model.

**Files:** `MainWindow.xaml`, possibly a few tooltip strings.

### Fix 8 — Once Fix 6 confirms bridge is audibly working, debug EQ

**Change:** once we know audio definitely reaches the user's ears through the bridge, retest EQ. If EQ is silent, the bug is in `BiQuadFilter.PeakingEQ` parameters or `MasterEqDsp.ProcessInline` math, not in the routing. Quick fixes to try:
- Replace `BiQuadFilter.PeakingEQ` with low-shelf + high-shelf for bands 0/9 (proven to work better than peaking on edge frequencies).
- Try a single test biquad with extreme gain (+24 dB) at 1 kHz to ensure the math is doing *anything*.
- Verify the buffer pointer math in `ProcessInline` matches the actual byte layout WASAPI hands us.

**Files:** `Core/MasterEqDsp.cs`.

---

## 4. What I'll ship next *(awaiting user approval)*

If the user agrees with this plan, the recommended order is:

1. **Fix 1 + Fix 5** *(small, high UX win)* — stop auto-engage breakage; add the "audio may need restart" toast.
2. **Fix 2 + Fix 3** *(small, prevents footgun)* — auto-pick previous default as ACTIVE; disable Set Default on hardware while bus is on.
3. **Fix 6** *(diagnostic, unblocks EQ debugging)* — Test bus output button proves the bridge is audibly working.
4. **Fix 4 + Fix 7** *(clarity, optional)* — routing indicator + chip rename.
5. **Fix 8** *(EQ math)* — only after Fix 6 confirms the bridge is delivering audible audio.

---

## 5. Open question for the user

**Should auto-engage-on-launch be removed entirely (Fix 1), or kept with a banner prompt?** Pick one of:

- **(a)** Remove auto-engage. Always start with bus OFF. User must click engage.
- **(b)** Keep auto-engage but show a "Bus engaged — restart audio if your app went quiet" toast.
- **(c)** Keep auto-engage but also auto-redirect via session APIs *(complex, may not work reliably)*.

I lean **(a)** for v1 because it gives the user clear control of when audio routing changes. Once the bus is reliable end-to-end, we can revisit auto-engage.
