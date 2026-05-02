# SonicFlow Driver Source

The actual driver source will live here after SysVAD is imported and reduced.

Do not hand-write a kernel audio driver from a blank file. Start from Microsoft's
SysVAD sample, build it unchanged once, then copy/rename the reduced SonicFlow
driver into this folder.

Target product identifiers:

- Driver workspace: `SonicFlowVirtualAudio`
- Render endpoint: `SonicFlow Virtual Speaker`
- Hardware ID: `Root\SonicFlowVirtualAudio`
- App contract: `Core/SonicFlowVirtualAudio.cs`

The first implementation should expose a simple render endpoint only. The user-mode
SonicFlow app can capture that render endpoint with WASAPI loopback and handle EQ,
mirroring, and per-output rendering.
