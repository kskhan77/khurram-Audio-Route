# SonicFlow Virtual Audio Driver

This folder is the product driver workspace for the future `SonicFlow Virtual Speaker`
endpoint. The WPF application is now wired to detect that render endpoint, set it as
the Windows default device, and use it as the single mirror/EQ source.

See `PLAN.md` for the product decision and milestone plan.

## Endpoint Contract

The app-side contract lives in `Core/SonicFlowVirtualAudio.cs`.

Expected render endpoint:

- Friendly name: `SonicFlow Virtual Speaker`
- Hardware ID: `Root\SonicFlowVirtualAudio`
- Role: Windows render endpoint used as the default playback device

Optional paired capture/monitor label:

- Friendly name: `SonicFlow Virtual Output`

The current C# pipeline can capture a render endpoint with WASAPI loopback, so the
first driver target only needs to expose a stable render endpoint. A paired capture
endpoint can be added later if we want a true cable-style "speaker in / recording
device out" flow.

## Driver Base

Use Microsoft's SysVAD sample as the starting point, then reduce it to one virtual
render endpoint:

- Sample: SysVAD Virtual Audio Device Driver
- Driver type: WDM audio miniport using WaveRT
- Starting endpoint: Tablet Audio Sample render endpoint
- Rename the package, device labels, hardware IDs, and INF manufacturer strings to
  SonicFlow names.

Do not put the kernel driver code inside the WPF project. Keep the WDK driver as its
own Visual Studio C++ solution and let the C# app interact with it through normal
Windows audio endpoint enumeration.

## Local Status

This machine now has the VS 2022 WDK toolchain needed for the driver build.

Current validated outputs:

- Staged driver source: `src/SonicFlowVirtualAudio/SonicFlowVirtualAudio`
- Driver binary: `SonicFlowVirtualAudio.sys`
- Stamped INF: `SonicFlowVirtualAudio.inf`
- Test certificate: `SonicFlowVirtualAudio.cer`
- Signed catalog: `SonicFlowVirtualAudio.cat`
- Clean test package: `out/x64/Debug`

## Build Path

1. Install Visual Studio C++ desktop workload and the Windows Driver Kit.
2. If MSBuild reports `WindowsKernelModeDriver10.0` missing, close Visual Studio
   Installer and run `.\install-vs2022-wdk-component.ps1`.
3. Run `.\import-sysvad.ps1`.
4. Run `.\build-driver.ps1` to build the unchanged SysVAD `TabletAudioSample` driver first.
5. Run `.\stage-sonicflow-driver.ps1`.
6. Use `RENAME_MAP.md` to rename the staged source to `SonicFlowVirtualAudio`.
7. Run `.\build-driver.ps1 -Staged`.
8. Run `.\package-driver.ps1`.
9. Install on a test machine with test signing enabled.
10. Confirm Windows Sound shows `SonicFlow Virtual Speaker`.
11. Open SonicFlow and use `Set Virtual Default`.

The full upstream SysVAD solution includes optional APO samples that require ATL.
The first SonicFlow driver does not need those APO projects because SonicFlow owns
EQ and DSP in the user-mode app.

## Test Install

Run these from an elevated PowerShell window after `package-driver.ps1` succeeds:

```powershell
.\install-test-driver.ps1 -EnableTestSigning
```

Reboot, then run:

```powershell
.\install-test-driver.ps1
```

If `bcdedit` reports that test signing is blocked, disable Secure Boot for driver
development or use a production driver-signing flow.

> Warning: do not disable Secure Boot on a BitLocker-encrypted machine without
> first suspending BitLocker (`manage-bde -protectors -disable C:`) or having
> the recovery key on hand. Toggling Secure Boot invalidates the TPM PCR seal
> and Windows will demand the recovery key on the next boot.

## Dev Fallback: VB-CABLE

When the SonicFlow driver cannot be installed locally (Secure Boot enforced on
a BitLocker-encrypted work laptop, no recovery key, etc.), use VB-Audio's
pre-signed VB-CABLE as the virtual bus during development. It installs without
test-signing and exposes the same render/capture endpoint pair the SonicFlow
driver eventually will.

1. Download the original VB-CABLE pack from <https://vb-audio.com/Cable/>.
   The free pack is enough; the A+B and C+D donationware extensions only add
   more independent cables (not needed for SonicFlow's single virtual bus).
2. Unzip and right-click `VBCABLE_Setup_x64.exe` -> Run as administrator.
3. Click **Install Driver**, accept the signed-driver prompt, reboot.
4. After reboot you should see `CABLE Input` under Sound -> Output and
   `CABLE Output` under Sound -> Input.

The SonicFlow app's `Core/SonicFlowVirtualAudio.cs` recognises VB-CABLE's
`CABLE Input` endpoint as the virtual bus alongside the shipped `SonicFlow
Virtual Speaker`, so no rebuild is needed - the device card and "Set Virtual
Default" command light up automatically.

This is the dev-only fallback documented in `PLAN.md`. The shipped product
must still ride on the SonicFlow-owned driver.

## Runtime Flow

```text
Windows apps
  -> SonicFlow Virtual Speaker
  -> SonicFlow loopback capture
  -> EQ / limiter / delay / optional per-output DSP
  -> selected physical render devices
```

Once the virtual endpoint is installed, physical output cards should stay standalone.
Only the virtual bus should host mirror target selection.
