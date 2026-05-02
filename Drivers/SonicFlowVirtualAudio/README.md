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

This machine currently has Visual Studio/MSBuild and Windows SDK files, but the WDK
driver build tools were not found:

- `Microsoft.DriverKit.props`
- `stampinf.exe`
- `devcon.exe`

Install the Windows Driver Kit before building or installing the driver package.

## Build Path

1. Install Visual Studio C++ desktop workload and the Windows Driver Kit.
2. Run `.\import-sysvad.ps1`.
3. Run `.\build-driver.ps1` to build the unchanged SysVAD sample first.
4. Open `upstream\Windows-driver-samples\audio\sysvad\sysvad.sln`.
4. Rename the sample to `SonicFlowVirtualAudio`.
5. Reduce the sample to one virtual render endpoint.
6. Set the INF hardware ID to `Root\SonicFlowVirtualAudio`.
7. Build x64 Debug first.
8. Install on a test machine with test signing enabled.
9. Confirm Windows Sound shows `SonicFlow Virtual Speaker`.
10. Open SonicFlow and use `Set Virtual Default`.

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
