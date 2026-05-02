# Room Impulse Responses

This folder is the drop-in location for room impulse responses used by
`Core/Spatial/SpatialPipeline.cs`'s `ConvolutionRoomStage`.

If a `<RoomName>.wav` exists here at runtime, the stage uses it. Otherwise it
falls back to an algorithmically generated IR with per-room parameters
(predelay, early reflections, RT60, exponential noise tail). The synthetic IRs
are good enough to ship; real recorded IRs sound noticeably better.

## File names the loader looks for

The file name must match the `RoomImpulseResponse` enum value exactly:

- `SmallStudio.wav`
- `SmallTheater.wav`
- `ConcertHall.wav`

`Dry` is handled in code (passthrough) and does not load a file.

## Required format

The loader is intentionally strict to keep startup fast and predictable:

- 32-bit IEEE float WAV **or** 16-bit PCM WAV
- Sample rate must match the engine's working rate (48 kHz for the current
  pipeline)
- Mono or stereo (stereo is downmixed to mono — same IR is applied to every
  input channel; per-ear / multichannel IRs would require more loader work)
- Trim to the actual decay tail. 0.5-1.5 s is plenty for studios, 2-3 s for
  halls. Longer = more CPU. The stage uses Cavern's `FastConvolver` (overlap-
  and-add FFT) so cost scales as O(N log N), but trimming silence still helps.

If a file violates any of these, the loader silently falls back to synthesis.
That's by design — the audio pipeline must never crash because an asset is
malformed.

## Where to get free, properly licensed IRs

- **OpenAir Library** (https://www.openair.hosted.york.ac.uk/) — CC-BY,
  hundreds of recorded spaces (cathedrals, theaters, studios, outdoor sites).
  Most files are 96 kHz multichannel; resample to 48 kHz mono before dropping
  here.
- **EchoThief** (https://www.echothief.com/) — CC-BY, unusual spaces
  (mausoleums, drains, cisterns).
- **IR-style packs from neural plugins** (e.g. Voxengo, Convology XT free
  packs) — check each pack's license before shipping.

## Quick prep recipe (Audacity)

1. Open the source IR.
2. `Tracks` -> `Resample` -> `48000 Hz`.
3. `Tracks` -> `Mix` -> `Mix Stereo Down to Mono` if stereo.
4. Trim the leading silence; trim the tail at -60 dB below peak.
5. `File` -> `Export` -> `Export as WAV` -> `WAV (Microsoft)` -> `32-bit float`.
6. Save as `Assets/IR/<RoomName>.wav` next to this README.

The csproj is set up to copy `Assets/IR/*.wav` to the build output, so a new
file is picked up on the next `dotnet build`.

## What you'll hear

- `SmallStudio` — short bright reverb, ~0.3 s tail.
- `SmallTheater` — medium decay, ~0.8 s tail with audible early reflections.
- `ConcertHall` — long lush decay, ~2 s tail.

Each room is mixed at `WetMix = 0.25` by default. Tune
`ConvolutionRoomStage.WetMix` per preset if the result is too dry/wet.
