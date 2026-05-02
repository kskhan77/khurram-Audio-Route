# SonicFlow Driver Source

The staged SonicFlow driver source now lives under `SonicFlowVirtualAudio/`.

Do not hand-write a kernel audio driver from a blank file. Start from Microsoft's
SysVAD sample, build it unchanged once, then copy/rename the reduced SonicFlow
driver into this folder.

Target product identifiers:

- Driver workspace: `SonicFlowVirtualAudio`
- Render endpoint: `SonicFlow Virtual Speaker`
- Hardware ID: `Root\SonicFlowVirtualAudio`
- App contract: `Core/SonicFlowVirtualAudio.cs`

The first build is renamed and packaged as `SonicFlowVirtualAudio`. Endpoint
reduction is still the next driver cleanup step; the user-mode SonicFlow app can
capture the primary render endpoint with WASAPI loopback and handle EQ, mirroring,
and per-output rendering.
