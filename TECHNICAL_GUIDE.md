# Audio Router V2 - Technical Documentation

## 🏗 Architecture Overview

Audio Router V2 is a high-performance Windows audio utility built with **.NET 10** and **WPF**. It follows a strictly decoupled **MVVM (Model-View-ViewModel)** pattern, bridging high-level C# orchestration with low-level Windows audio internals.

### 🧩 Core Components

| Component | Responsibility | Primary Class |
| :--- | :--- | :--- |
| **Native Routing** | Interacts with undocumented Windows COM interfaces to move apps between devices. | `AudioRouterNative.cs` |
| **Duplication Engine** | Handles real-time audio mirroring with latency and EQ compensation. | `DuplicationManager.cs` |
| **DSP Engine** | Provides professional-grade EQ and signal processing via BASS. | `BassEngine.cs` |
| **Hardware Manager** | Enumerates and monitors WASAPI audio endpoints. | `DeviceManager.cs` |
| **Session Manager** | Discovers and controls individual application audio streams. | `SessionManager.cs` |

---

## 🎧 Audio Engine Logic

### 1. Per-App Routing (The "Bridge")
The routing logic bypasses standard Windows UI by directly calling the `IPolicyConfig` and `IAudioPolicyConfigFactory` COM interfaces.
- **Windows 11 Support**: Uses PID-based routing with manual `HSTRING` handle management via `combase.dll`.
- **Windows 10 Support**: Fallback to path-based routing for legacy builds.
- **Migration Force**: After a routing change, the app executes a Mute → Wait (250ms) → Unmute sequence. This forces the application's audio client to re-initialize and migrate to the new endpoint immediately.

### 2. Low-Latency Duplication (The "Mirror")
Duplication uses a "Capture once, Fan-out" architecture:
1. **Capture**: A `WasapiLoopbackCapture` instance listens to the source device's output.
2. **Buffer**: Audio data is fed into a `BufferedWaveProvider`.
3. **Processing**:
   - **Resampling**: Uses `WdlResamplingSampleProvider` if sample rates mismatch.
   - **Equalization**: `EqualizerSampleProvider` applies a 10-band ISO-octave EQ.
   - **Latency Sync**: `DelaySampleProvider` inserts a ring-buffered delay (up to 800ms) to align wired glasses with Bluetooth devices.
4. **Playback**: Independent `WasapiOut` players deliver the processed stream to target devices.

### 3. DSP & Enhancement (BASS)
The `BassEngine` provides a professional alternative to the NAudio pipeline:
- **Loopback**: Captures system sound via `BassWasapi`.
- **Mixer**: Feeds captured audio into a `BassMix` stream.
- **EQ**: Applies high-precision `PeakEQ` effects.
- **Test Tones**: Generates professional-grade noise profiles for diagnostic verification.

---

## 🛠 Developer Guide

### Prerequisites
- **Native DLLs**: `bass.dll`, `bassmix.dll`, and `bass_fx.dll` must reside in the `Native/` directory or the executable root.
- **Framework**: .NET 10 SDK.

### Initialization Sequence
1. `App.xaml.cs` starts the application.
2. `MainViewModel` is instantiated as the `DataContext` for `MainWindow`.
3. `RefreshData()` is called to populate the device and session lists.
4. A 220ms `CompositionTarget.Rendering` or Timer-based loop starts `RefreshMeters()`, which polls the WASAPI peak values and updates the UI.

### Adding a New Processor
To add a new DSP effect:
1. Implement the effect in `Core/BassEngine.cs` (for global) or as an `ISampleProvider` in `Core/DuplicationManager.cs` (for mirrored).
2. Add a property in `AudioDevice.cs` to hold the parameter.
3. Update `MainViewModel.OnOutputDevicePropertyChanged` to push the new parameter into the engine.

---

## 📝 Future Documentation (DocFX)
This project is structured for **DocFX** integration. To generate full API documentation:
1. Install DocFX: `dotnet tool install -g docfx`.
2. Run `docfx init` to create a configuration.
3. Use `docfx metadata` to extract XML comments from the code.
4. Use `docfx build` to generate the HTML documentation site.
