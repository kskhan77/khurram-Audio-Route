# Khurram Audio Route Implementation Plan

## Objective
To replace the legacy, highly brittle C++ `audio-router` codebase with a modern, stable, and feature-rich application built in C# (.NET). The new application will resolve all Windows 11 compatibility issues related to audio session hooking and provide an extensible foundation for routing, duplication, and DSP (Digital Signal Processing).

## Background & Motivation
The current Audio Router relies on injecting DLLs into foreign processes and hooking internal WASAPI methods (like `IAudioClient::Initialize`). This approach is actively hostile to modern OS security features (especially in Windows 11) and modern app architectures (like UWP, Chrome, and Edge, which constantly spawn and kill sub-processes for media playback). 

Fortunately, Windows 10 (1803+) and Windows 11 introduced undocumented COM interfaces (`IAudioPolicyConfig`) that allow seamless, OS-level, per-app routing without any injection. By moving to a modern C# stack, we can leverage these native APIs for simple routing, and use robust Managed Audio Libraries (like NAudio or CSCore) for complex loopback capture, duplication, and EQ.

## Scope & Impact
The new C# application will have the following feature scope:
- **Core Routing:** Seamlessly move an application's audio output to a different device using native Windows APIs.
- **Audio Duplication:** Capture an application's audio stream via WASAPI Loopback and play it back simultaneously across multiple audio devices.
- **Microphone Routing:** Support for capturing input devices and routing them to virtual cables or output endpoints.
- **Sound Controller (EQ):** Integrate basic DSP for equalizer adjustments per audio stream.
- **Soundboard Feature:** Allow the user to play pre-loaded sound files over a specific output device or virtual cable.
- **Portability:** Provide a fully self-contained `.exe` (via .NET Single-File Deployment) that does not require installation, while also offering an optional MSIX installer for those who want system tray integration and start menu shortcuts.

## Proposed Solution
We will develop a modern **WPF (Windows Presentation Foundation) Application** targeting **.NET 8+**.
1. **Routing Engine:** Use P/Invoke to consume the undocumented `IAudioPolicyConfig` interface (`{9119BD0D-2D89-41A4-BB09-29794029744E}`). This provides 100% reliable per-app routing on Windows 11 without any performance overhead or app crashing.
2. **Duplication/Loopback Engine:** Use the **NAudio** library. When the user requests "Duplication", we will initialize a `WasapiLoopbackCapture` instance targeting the specific process (or the default render endpoint, filtered by process session), buffer the audio, and route it to multiple `WasapiOut` instances.
3. **UI/UX:** A clean, modern WPF interface with a system tray component for quick access. 
4. **DSP & Mixing:** NAudio provides excellent `ISampleProvider` interfaces to build an EQ pipeline and mix soundboard clips seamlessly into an outgoing audio stream.

## Alternatives Considered
- **Fixing the existing C++ Codebase:** Rejected. The DLL injection model is inherently flawed on modern Windows OS versions and will continue to break with every major Windows update.
- **Using Virtual Audio Cables globally:** Rejected. This requires complex driver installations and system-wide changes, moving away from the requested portability goal.

## Phased Implementation Plan

### ✅ Phase 1: The Core Foundation (Routing & UI) - COMPLETED
- [x] Initialize a new .NET 10 WPF project structure.
- [x] Build the core UI (App list, Volume sliders, Device selection dropdowns).
- [x] Implement the native Windows 11 Routing using `IAudioPolicyConfig` COM interop.
- [x] Setup MVVM architecture with `CommunityToolkit.Mvvm`.

### ✅ Phase 2: The Duplication Engine - COMPLETED
- [x] Integrate the NAudio library.
- [x] Implement the WASAPI Capture engine.
- [x] Implement the multi-device playback engine.

### 🚧 Phase 3: Advanced Audio (EQ & Mic) - NEXT
- [ ] Implement `ISampleProvider` wrappers in NAudio to apply real-time Equalization.
- [ ] Add support for routing and duplicating Microphone (Capture) inputs.
- [ ] Implement per-process loopback (Visual Studio/Windows 11 SDK required for activation params).

### 📅 Phase 4: Soundboard & Portability - FUTURE
- [ ] Build the Soundboard UI for loading and triggering short audio files.
- [ ] Implement Global Hotkeys for Soundboard.
- [ ] Configure the .NET project for Single-File Deployment.
- [ ] Implement System Tray "Minimize to tray" and persistence.

## Verification & Testing
- **Unit Testing:** Write C# unit tests for the configuration parser and the audio buffering logic.
- **Integration Testing:** Test routing Chrome, Edge, and Spotify to separate output devices simultaneously to ensure Windows 11 compatibility.
- **Latency Testing:** Measure the round-trip latency of the Duplication engine to ensure it remains under 50ms to prevent an echo effect.

## Migration
- The old C++ source tree will be retained in a `legacy/` directory for reference but will no longer be actively compiled. 
- The new project will live in `src/KhurramAudioRoute/`.