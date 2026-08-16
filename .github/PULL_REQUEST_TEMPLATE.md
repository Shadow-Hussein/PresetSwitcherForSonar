# What this changes

<!-- One or two sentences. Link the issue with "Closes #123" if there is one. -->

## Why

<!-- The problem being solved. -->

## How it was verified

There is no test project — verification is manual, against a live SteelSeries GG.
Tick what you actually did:

- [ ] `dotnet build "SonarPresetSwitcher.sln" -c Debug` succeeds with no new warnings
- [ ] Ran the app against a running GG with the Sonar sub-app enabled
- [ ] A real preset switch fired and produced a toast
- [ ] Preset ordering is still favourites-first, then alphabetical
- [ ] App still degrades gracefully (no crash) with GG closed
- [ ] Settings still round-trip through `%APPDATA%\SonarPresetSwitcher\settings.json`

If this touches **per-app audio routing** (`AudioPolicyConfig`, `AudioRoutingService`):

- [ ] Settings → Sound → Volume mixer shows the app on the chosen device
- [ ] The **system** default device is unchanged
- [ ] No unknown COM vtable slot was probed on a live machine

If this touches the **system default device** (`PolicyConfig`, `DefaultDeviceGuard`):

- [ ] With the option off, nothing writes the system default — GG still wins
- [ ] With it on, the default returns to the chosen device after GG or the Sound panel moves it
- [ ] The **communications** default is unchanged
- [ ] Unplugging the held device does not send Windows at a dead endpoint

## Notes for the reviewer

<!-- Anything surprising: an empirically-established interop layout, a runtime-only trap,
     a decision that looks wrong but is not. -->
