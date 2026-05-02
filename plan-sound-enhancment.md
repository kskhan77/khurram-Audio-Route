# SonicFlow Sound Enhancement Plan

This document outlines the transition from basic audio routing to high-end, professional-grade sound processing using **BASS**, **Cavern**, and **Windows Spatial Audio**.

## 1. Current Limitation: Why EQ only works on "Mirrored" devices
Currently, the application uses **NAudio WasapiLoopbackCapture**. 
- It "listens" to the audio already playing on a source device.
- It then creates a **new** stream, applies the EQ, and sends it to the **Target** devices.
- **The Problem:** It cannot "intercept" the sound on the original single device without creating a loop (feedback). To fix this, we need a **Global DSP Hook** or a **Virtual Audio Cable** approach, which is where the new libraries come in.

---

## 2. Professional Libraries Comparison

| Library | Role in SonicFlow | Key Features |
| :--- | :--- | :--- |
| **BASS (ManagedBass)** | **Core Engine & Sync** | Professional 31-band EQ, micro-latency sync between 5.1/7.1 setups, and matrix mixing. |
| **Cavern** | **3D & Atmos Rendering** | Object-based audio. Can take Stereo and "upmix" it to 7.1.4 or virtualize 7.1 onto Headphones. |
| **Windows Spatial** | **System Integration** | Uses the native Windows 11/10 "Spatial Sound" engine for hardware-accelerated Dolby Atmos. |

---

## 3. Enhancement Roadmap

### Phase 0: SonicFlow Virtual Driver
- **Goal:** Create a product-owned Windows playback endpoint named `SonicFlow Virtual Speaker`.
- **Method:** Start from Microsoft's SysVAD WDK sample, reduce it to one virtual render endpoint, and expose it with hardware ID `Root\SonicFlowVirtualAudio`.
- **App Flow:** Set the virtual endpoint as default, capture it with WASAPI loopback, process EQ/limiter/delay, then fan out to selected physical devices.
- **UI Rule:** Once the virtual endpoint is installed, mirroring belongs only to the virtual device card. Physical output cards stay standalone.

### Phase 1: BASS Integration (Single Device EQ)
- **Goal:** Enable EQ even if only one device is used.
- **Method:** Instead of Loopback, we will initialize the output device through BASS.
- **Feature:** 30-band Graphic EQ and Peak Limiter (to prevent distortion).
- **No-Break Guarantee:** The duplication (mirroring) buttons and sliders in the UI will stay exactly the same. We are only changing the "engine" under the hood. Existing NAudio logic will be kept as a backup until BASS is fully tested.

### Phase 2: Multi-Device Precision Sync & Duplication
- **Goal:** Upgrade duplication to BASS for perfect alignment.
- **Improvement:** By moving duplication to the BASS Mixer, the EQ will finally work on the **Main Speaker** and the **Mirrored Speakers** at the same time.

### Phase 3: Spatial Audio & 3D (Cavern)
- **Goal:** "Cinema Sound" for any device.
- **Features:**
    - **Virtual Surround:** 7.1 environment for Stereo Headphones.
    - **Upmixing:** Turning standard YouTube/Spotify music into immersive 5.1.
    - **Room Modeling:** Simulate the acoustics of a studio or theater.


### Phase 4: Dolby Atmos (Native)
- **Goal:** Official Atmos support.
- **Method:** Integrate `ISpatialAudioClient` to route object-based audio directly into the Windows spatial pipeline.

---

## 4. Technical Requirements
- **Native DLLs:** `bass.dll`, `bassmix.dll`, `bass_fx.dll` must be included in the application folder.
- **CPU Usage:** Professional DSP requires more CPU. We will implement "Quality Profiles" (Low, Medium, High).
