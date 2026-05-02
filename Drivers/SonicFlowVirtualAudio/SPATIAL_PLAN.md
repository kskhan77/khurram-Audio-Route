# SonicFlow Spatial Audio Plan (Phase 3)

## Goal

Make SonicFlow upscale ordinary stereo content into immersive surround / 3D
audio for whichever output the user is on. Headline features:

- **Virtual Surround** - 7.1 environment for stereo headphones (HRTF binaural).
- **Upmixing** - turn standard YouTube/Spotify stereo into 5.1/7.1.
- **Room Modeling** - simulate the acoustics of a studio, theater, or hall.

This plan keeps SonicFlow in pure C# / .NET 10, free / OSS-licensed, and runs
without driver work or ML-stack hosting.

## Library Stack

| Layer | Library | License | Why |
| --- | --- | --- | --- |
| Spatial engine | **Cavern** + **Cavern.Format** (NuGet) | MIT | Native C#, designed for object-based / surround / HRTF rendering, drop-in for our stack |
| Room IRs | **OpenAirLibrary.com** sample set | CC-BY | Free, real recorded spaces (cathedrals, theaters, studios) |
| Convolution | Cavern.Filters or hand-rolled partitioned FFT | n/a | Cheap, deterministic, fits the per-channel pipeline we already have |
| Upgrade path (later) | **Steam Audio** | Apache 2.0 | Adds ray-traced reflections + occlusion; only if Cavern's room model is insufficient |

Already added to `KhurramAudioRoute.csproj`:

```xml
<PackageReference Include="Cavern" Version="2.1.0" />
<PackageReference Include="Cavern.Format" Version="2.1.0" />
```

## Pipeline

```text
App  ->  CABLE Input (or SonicFlow Virtual Speaker once installed)
     ->  WASAPI loopback capture                 (existing)
     ->  Stage A: Upmix          (Cavern Upconvert)
     ->  Stage B: Scene          (place channels in 3D layout)
     ->  Stage C: Room model     (convolution IR)
     ->  Stage D: Renderer       (Cavern HRTF for headphones, or N-ch direct)
     ->  Stage E: EQ / limiter   (existing pipeline)
     ->  selected physical outputs
```

Each stage is independently toggleable. The scaffolding lives in
`Core/Spatial/SpatialPipeline.cs` and uses the `ISpatialStage` contract so
backends can be swapped (Cavern -> Steam Audio etc.) without touching call
sites.

## Presets

Users pick a preset; advanced users break the stages out and tune.

| Preset | Upmix | Scene | Room | Renderer |
| --- | --- | --- | --- | --- |
| Off (transparent) | - | - | - | passthrough |
| Headphone Stereo+ | - | - | - | BS2B crossfeed |
| Headphone Cinema | 2 -> 7.1 | cinema layout | small theater IR | HRTF binaural |
| Headphone Studio | - | front L/R | small studio IR | HRTF binaural |
| Headphone Concert Hall | 2 -> 5.1 | concert layout | large hall IR | HRTF binaural |
| 5.1 Speakers | 2 -> 5.1 | matched layout | dry | direct 6-channel |
| Game Mode (low latency) | - | - | - | minimal HRTF |

The enum lives at `Core.Spatial.SpatialPreset`; `SpatialPipelineFactory.Create`
turns a preset into a stage list.

## Caveats To Surface In The UI

- Generic HRTFs are mediocre - "out of head" effect varies per listener.
  Plan for an A/B HRTF switcher (SADIE-II vs MIT KEMAR vs IRCAM).
- Convolution adds (IR length / 2) latency unless we use partitioned
  convolution. Music: fine. Games: keep IRs short or skip the room.
- Per-channel binaural + convolution costs roughly 5-15% of one core on
  modern laptops. Profile before shipping.
- Upmix can hurt already-spatial content (Atmos masters, intentional stereo
  classical). Expose a "bypass on multichannel input" toggle and detect mono
  sums to skip phantom-center creation.

## Phases

### 3.1 - Wire Cavern in (skeleton)

Status: scaffold landed in `Core/Spatial/SpatialPipeline.cs`.

- `ISpatialStage` contract, `SpatialBuffer` carrier struct, `SpatialPreset`
  enum, `SpatialPipelineFactory` shipping. All stages stubbed; build is green.

### 3.2 - Cavern HRTF passthrough

Status: implemented (not yet wired into the live audio path).

- `CavernBinauralStage.Process` builds a `Cavern.Listener` with
  `HeadphoneVirtualizer = true`, fans input channels to positioned
  `StreamMasterSource`s at standard speaker angles, and renders 2-channel
  binaural per call.
- Layout-aware speaker placement: stereo (+/-30deg), 5.1, 7.1.
- Defensive copy of Cavern's render output so callers don't share state.

### 3.3 - Convolution room model

Status: implemented with algorithmic IRs (real-IR drop-in supported).

- `ConvolutionRoomStage.Process` runs Cavern's `FastConvolver` per channel,
  mixes wet/dry per `WetMix`, returns same-layout buffer.
- IR loader prefers `Assets/IR/<RoomName>.wav` (32-bit float or 16-bit PCM,
  pipeline sample rate, mono / stereo-downmix). Falls back to algorithmic
  synthesis (predelay + sparse early reflections + exponential noise tail) if
  the file is missing or invalid - the stage works out of the box.
- Per-room parameters: SmallStudio (0.4 s, RT60 0.3 s),
  SmallTheater (1.0 s, RT60 0.8 s), ConcertHall (2.5 s, RT60 2.0 s).
- `Assets\IR\*.wav` is copied to build output via csproj `<Content>` item
  so dropped-in IRs are picked up on next build. README at `Assets/IR/README.md`
  documents the format and prep recipe.

### 3.4 - Upmix

Status: implemented as a hand-rolled matrix (`MatrixUpmixStage`).

- Hafler-style derivation: FC = (L+R)/sqrt(2), BL/BR = +-(L-R)/sqrt(2),
  LFE = 0. For 7.1, SL/SR mirror BL/BR at -3 dB.
- Bypassed when input is already multichannel (ChannelCount >= 3).
- Cavern's `SurroundUpmixer` was evaluated but its callback-based
  `OnSamplesNeeded` / `IntermediateSources` model didn't fit the
  `SpatialBuffer Process(...)` contract without significant adapter code
  and per-frame allocation. The matrix path can be swapped for Cavern's
  later if quality demands it.
- Channel order matches `CavernBinauralStage`'s expected layout
  (5.1: FL,FR,FC,LFE,BL,BR; 7.1: FL,FR,FC,LFE,SL,SR,BL,BR — Cavern
  convention, not Microsoft WaveFormatExtensible).

### 3.5 - Preset UI

Status: pending.

- Dropdown on the virtual-bus card (`CABLE Input` / `SonicFlow Virtual
  Speaker`) bound to `SpatialPreset`.
- Live A/B between Off and the active preset.
- Save selected preset to user settings.

### 3.6 - Quality polish

Status: pending.

- Add HRTF database switcher (free SOFA files: SADIE-II, MIT KEMAR).
- Profile CPU on the per-target mirror path; ensure room+HRTF stays under
  20% of one core during 7.1 -> binaural rendering.
- Optional: Steam Audio behind a feature flag for advanced users.

## Out Of Scope For Phase 3

These belong to Phase 4 or later:

- ML stem separation (Demucs / Spleeter via ONNX).
- Personalized HRTF (photo-based ear estimation).
- Atmos / object-based decode (requires Microsoft / Dolby licensing).
- Per-source 3D placement based on content analysis.

## Files / Symbols To Know

| Path | Purpose |
| --- | --- |
| `Core/Spatial/SpatialPipeline.cs` | Stage contract, preset factory, all stub stages |
| `Core/SonicFlowVirtualAudio.cs` | Virtual-bus detection (recognises VB-CABLE today) |
| `Core/BassEngine.cs` | Existing capture + EQ pipeline; the spatial pipeline plugs in before Stage E |
| `KhurramAudioRoute.csproj` | `Cavern` + `Cavern.Format` package refs |
| `Drivers/SonicFlowVirtualAudio/PLAN.md` | Driver-side phases (Phase 1-5) |
