# Audio Router V2 Plan

## Current Direction
- `Applications` should be route-only.
- `Outputs` should become the home for default-device management and future device duplication.
- `Mic Devices` should show capture devices now and gain routing/monitoring later.
- Advanced device controls like volume, EQ, and duplication should stay collapsed by default to save space.

## Done
- [x] Modernized the WPF shell into a dark dashboard layout.
- [x] Added left navigation with `Outputs`, `Applications`, `Mic Devices`, and `Other Options`.
- [x] Made `Outputs` the default page on startup.
- [x] Restored `Applications` to route-only UI with per-app dropdown plus `Route` button.
- [x] Added live application level meters and volume sliders.
- [x] Added microphone device discovery and a separate mic page.
- [x] Added compact output cards with explicit `Set Default` action.
- [x] Added responsive startup sizing so the window opens around 60% of screen height.
- [x] Added collapsed `Advanced` space on output cards for future volume, EQ, and duplicate controls.
- [x] Fixed the XAML parse crash caused by the invalid `Equalizer24` symbol token.

## In Progress
- [ ] Output cards still have placeholder EQ controls.
- [ ] Device duplication needs polish for per-target toggles and persistent state.
- [ ] Device volume in the output advanced section is display-only.

## Next Task
- [x] Implement device-level duplication from the `Outputs` page.
- [x] Make source device and target devices explicit in the UI.
- [x] Key duplication sessions by source device instead of app/process.
- [ ] Allow one target device to be turned off without stopping the whole source session when other targets remain active.

## After Next Task
- [x] Replace the disabled `Duplicate` toggle with real mirror and duplicate actions.
- [ ] Add writable output-device volume control.
- [ ] Add output-device latency and buffer tuning options.
- [ ] Add actual EQ or DSP pipeline if the advanced section remains part of the design.
- [ ] Add optional persistent expanded and collapsed advanced-card state if needed.

## Later
- [ ] Add microphone routing and monitoring behavior.
- [ ] Improve duplication latency and synchronization stability.
- [ ] Add soundboard and utility tools if still wanted.
- [ ] Add tray and minimize behavior plus packaging improvements.

## Testing Checklist
- [ ] Route Chrome to Device A and Edge to Device B.
- [ ] Confirm both apps stay isolated when no duplication is active.
- [ ] Duplicate Device A to Device C and confirm only Device A audio is mirrored.
- [ ] Turn off one duplicate target and confirm other targets continue.
- [ ] Validate default-device switching still works after duplication changes.
