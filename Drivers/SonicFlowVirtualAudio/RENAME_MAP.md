# SonicFlow Driver Rename Map

This maps the SysVAD `TabletAudioSample` baseline into the first SonicFlow virtual
audio driver source tree.

## Project Names

| SysVAD | SonicFlow |
| --- | --- |
| `TabletAudioSample` | `SonicFlowVirtualAudio` |
| `ComponentizedAudioSample` | `SonicFlowVirtualAudio` |
| `SYSVAD` | `SonicFlow` |

## Device Contract

| Field | Value |
| --- | --- |
| Render endpoint name | `SonicFlow Virtual Speaker` |
| Hardware ID | `Root\SonicFlowVirtualAudio` |
| Provider | `SonicFlow` |
| Manufacturer | `SonicFlow` |
| Service name | `SonicFlowVirtualAudio` |
| Driver binary | `SonicFlowVirtualAudio.sys` |

## First Reduction

Keep only the render endpoint needed for the virtual speaker path. Remove APO sample
projects from the first SonicFlow build because the app owns EQ/DSP in user mode.

Keep:

- `TabletAudioSample`
- `EndpointsCommon`
- Shared root source files used by the tablet sample

Remove or ignore for first product driver:

- `APO/*`
- `KeywordDetectorAdapter`
- Bluetooth/USB sideband endpoints after the first plain render path is verified

## Runtime Target

```text
Windows apps -> SonicFlow Virtual Speaker -> SonicFlow WPF loopback/DSP -> physical outputs
```
