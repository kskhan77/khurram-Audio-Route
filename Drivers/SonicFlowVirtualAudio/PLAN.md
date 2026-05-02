# SonicFlow Virtual Audio Plan

## Decision

Build a SonicFlow-owned virtual audio driver for the product.

Third-party virtual cable drivers are allowed only as temporary development/test
fallbacks while the SonicFlow driver is not ready. They should not be required for
the finished product experience.

## Why

- The equalizer must work before audio reaches any physical speaker/headphone.
- Windows will not let a normal C# desktop app create a real playback endpoint.
- NAudio and BASS can capture/process/render audio, but the virtual sound card must
  be a Windows driver.
- Owning the driver gives us a stable product endpoint named `SonicFlow Virtual Speaker`.

## Product Runtime

```text
Windows apps
  -> SonicFlow Virtual Speaker
  -> SonicFlow capture/DSP engine
  -> selected physical output devices
```

The WPF app already knows how to detect the future virtual endpoint through
`Core/SonicFlowVirtualAudio.cs`.

## Phases

### Phase 1 - App Contract

Status: implemented for the first UI/device contract.

- Detect `SonicFlow Virtual Speaker`.
- Show the virtual-device status card.
- Set the virtual endpoint as the Windows default device.
- Put mirror controls only on the virtual-device card when installed.
- Keep physical cards standalone.

### Phase 2 - Driver Import

Status: SonicFlow rename/package builds; endpoint reduction still pending.

- Install the Windows Driver Kit.
- Import Microsoft's SysVAD sample.
- Reduce it to one render endpoint.
- Rename it to SonicFlow. Done for the staged build.
- Set hardware ID to `Root\SonicFlowVirtualAudio`. Done.
- Set endpoint name to `SonicFlow Virtual Speaker`. Done for the primary render endpoint.

### Phase 3 - First Test Driver

Status: x64 Debug SonicFlow package builds and test-signs.

- Build x64 Debug. Done.
- Generate and sign `SonicFlowVirtualAudio.cat`. Done.
- Enable test signing on a test Windows machine.
- Install the package with DevCon.
- Verify the endpoint appears in Windows Sound settings.
- Verify SonicFlow detects it and can set it as default.

### Phase 4 - Audio Bridge

Status: pending.

- Capture the virtual endpoint with WASAPI loopback.
- Apply EQ, limiter, and delay alignment.
- Render processed audio to selected physical endpoints.
- Save selected mirror targets and EQ profile.

### Phase 5 - Product Packaging

Status: pending.

- Driver package signing.
- Installer flow for the app plus driver.
- Upgrade/uninstall handling.
- Device name migration if an older test driver is installed.

## Temporary Third-Party Option

Use VB-CABLE, Voicemeeter, Virtual Audio Cable, or similar only for early validation.
That lets us test the SonicFlow app-side bus flow before the custom driver is built.

Temporary test flow:

```text
Windows apps -> third-party virtual cable -> SonicFlow DSP -> physical outputs
```

This is not the final product path.
