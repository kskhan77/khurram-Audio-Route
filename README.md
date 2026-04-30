# Khurram Audio Route

## 🚀 Quick Technical Overview
This is a modern rewrite of Audio Router for Windows 11. Unlike the original, it uses **Zero Injection**. It does not modify other programs; instead, it uses native Windows COM interfaces and WASAPI Loopback.

### Architecture (MVVM)
- **Views**: `MainWindow.xaml` - The UI layout.
- **ViewModels**: `MainViewModel.cs` - Logic connecting the UI to the Audio engine.
- **Core (The "Brain")**:
  - `AudioRouterNative.cs`: Uses undocumented Windows API `IAudioPolicyConfig` to move apps between devices.
  - `DeviceManager.cs`: Handles listing speakers/headphones using the **CSCore** library.
  - `SessionManager.cs`: Finds which apps are currently playing sound.
  - `DuplicationManager.cs`: Uses **NAudio** to capture audio from an app and play it to a second device.

## 🛠 Project Status (v1.0-Alpha)

### ✅ COMPLETED (Phase 1 & 2)
- [x] **Native Windows 11 Routing**: Move Chrome/Edge/Spotify to different devices without crashes.
- [x] **Audio Duplication**: Play one app on two devices at once (Basic Engine).
- [x] **Modern UI**: WPF-based list with auto-refresh and device selection.
- [x] **Portable Build**: Set up for .NET 10 "Single File" deployment.

### 🚧 IN PROGRESS / TODO (Phase 3 & 4)
- [ ] **Advanced Duplication (Phase 3)**:
  - [ ] Per-process loopback (currently captures global device audio for simplicity).
  - [ ] Multi-device sync (reduce echo/latency).
- [ ] **Sound Controller (EQ)**:
  - [ ] Add 10-band Equalizer for duplicated streams.
- [ ] **Microphone Features**:
  - [ ] Route/Duplicate Mic input to output devices.
- [ ] **Soundboard**:
  - [ ] Hotkey support to play MP3/WAV files into specific channels.
- [ ] **UI Polish**:
  - [ ] Add app icons to the list.
  - [ ] System Tray "Minimize to tray" support.

## 📖 Developer Reference
- **Adding a New Feature**: 
  1. Add the core logic in `Core/`.
  2. Add a `RelayCommand` in `MainViewModel.cs`.
  3. Bind a `Button` in `MainWindow.xaml`.
- **Why CSCore + NAudio?**: 
  - `CSCore` is used for high-level Device/Session management.
  - `NAudio` is used for the complex Buffer/Duplication logic because of its superior `BufferedWaveProvider`.

---
*Created April 29, 2026*
