# Sonar Preset Switcher

[![Build](https://github.com/Shadow-Hussein/SonarPresetSwitcher/actions/workflows/build.yml/badge.svg)](https://github.com/Shadow-Hussein/SonarPresetSwitcher/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform: Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4)
![.NET 10](https://img.shields.io/badge/.NET-10.0--windows-512BD4)

A small WPF tray app that automatically switches the **SteelSeries Sonar Game-channel preset** when a
game launches, and restores a default preset when no mapped game is running.

![The Mapped presets tab: five games bound to Sonar presets and output devices](docs/screenshots/mapped-presets.png)

*Each row binds a process to a Game preset and, optionally, to a Windows output device. The row for a
game that is running is marked, and the most recently started one wins.*

## Install

Grab the latest zip from [Releases](https://github.com/Shadow-Hussein/SonarPresetSwitcher/releases),
extract it anywhere, and run `SonarPresetSwitcher.exe`. The release build is self-contained, so no .NET
runtime install is needed. To build from source instead, see below.

## Build & run

```bash
dotnet build "SonarPresetSwitcher.sln"
```

The executable lands in `src/SonarPresetSwitcher/bin/Debug/net10.0-windows/SonarPresetSwitcher.exe`.
Pass `--minimized` to start straight to the tray (this is what the "Start with Windows" toggle registers).

Requires .NET 10 Desktop Runtime and SteelSeries GG running with the Sonar sub-app enabled.

## How it talks to Sonar

Sonar exposes an unauthenticated local REST API, but on a port that changes every time GG launches, so
it has to be discovered at runtime:

1. Read `ggEncryptedAddress` from `C:\ProgramData\SteelSeries\GG\coreProps.json`.
2. `GET https://{ggEncryptedAddress}/subApps` — HTTPS with a self-signed SteelSeries certificate.
3. Take `subApps.sonar.metadata.webServerAddress`, e.g. `http://127.0.0.1:50453`.

Against that base URL:

| Method | Route | Used for |
|---|---|---|
| `GET` | `/configs` | every preset, with `isFavorite` and `favoritePosition` |
| `GET` | `/configs/selected` | the active preset per channel |
| `PUT` | `/configs/{id}/select` | switching the preset |
| `GET` | `/mode` | liveness probe, and classic vs. streamer mixer mode |
| `GET` | `/classicRedirections` | which Windows endpoint each channel plays out of |
| `GET` | `/audioDevices` | every endpoint Sonar knows, with `isVad` |
| `PUT` | `/configs` | saving a whole config — the only way to set `data.generalGain` |
| `GET` | `/v1/chatMix` | the game/chat balance, and whether ChatMix exists on this setup |
| `PUT` | `/v1/chatMix?balance=` | setting the balance |

ChatMix is the one route with a `/v1` prefix — `/chatMix` is a 404 — and its balance binds from the
query string. Sent as a JSON body instead, Sonar answers `200` and writes `0`, so a body would look
like it worked and silently centre the mix.

Any transport failure invalidates the cached URL and the call is retried once against a freshly
discovered endpoint, so restarting GG (which hands Sonar a new port) heals without restarting the app.

## Switching rules

- The most recently started mapped game wins.
- When it exits, the app falls back to the next mapped game still running, then to the default preset.
- A switch is only issued when the target differs from what Sonar reports as selected, so a preset you
  pick by hand in GG mid-game is not overridden.
- Mapped games that were already running when the app starts are picked up on the first poll.
- If a mapped preset is un-starred in GG, the row is flagged and switching is skipped for it.

## Detection

`ProcessWatcher` polls the process table (default every 2s) and diffs process names. Polling rather
than WMI events: no WMI permissions needed, no COM lifetime issues, and the first tick naturally
catches games that were already running. Reading `Process.ProcessName` needs no elevation, so
anti-cheat titles running elevated are still detected.

## Holding the Windows default playback device

SteelSeries GG claims the Windows default playback device for its own "Sonar - Gaming" virtual device
every time it starts, so a default you set by hand is gone after the next reboot. GG has no setting to
turn that off.

**Settings → "Hold Windows default"** ticks that back. Pick an output device, and `DefaultDeviceGuard`
puts the system default back to it whenever anything moves it — checking every 2s, and slowing down
rather than fighting if something keeps taking it.

Two things to know:

- This is the only part of the app that writes to Windows itself rather than a per-app override. While
  it is on, changing the default from Windows' Sound settings will not stick; untick it first.
- Only the Console and Multimedia roles are set, the pair behind "Set as Default Device". The
  *communications* default is left alone, so Sonar keeps its Chat device for calls.

Off by default. If you would rather not touch the system default at all, the per-app output device
column does the same job for individual games without it.

## Per-device Volume Boost

Sonar's **Volume Boost** — the 0 to 12 dB slider above the equalizer, applied before processing — is
stored per *preset*, not per device. Send the Game channel from a headset to speakers and the headset's
boost comes with it, so a level that suits one output is wrong on the other.

![The Additional features tab, with a Game and Media boost saved for two output devices](docs/screenshots/volume-boost.png)

*A boost per device and per channel — the speakers get 4.5 dB on Game and 3 dB on Media, the headset 6 dB
on Game and nothing on Media. "Restore to 0 dB" puts a preset back when a device stops using it.*

**Sonar Volume Boost per device** saves a Game and a Media boost for each output device. When Sonar
starts playing that channel through it, `ChannelBoostService` applies the saved dB. The two channels are
independent: a device can hold 6 dB for Game and 3 dB for Media, and leaving a box empty leaves that
channel alone entirely. Sonar's own virtual endpoints are kept out of the picker — boosting the channel
that feeds "Sonar - Gaming" would be circular.

**The boost is written into the preset.** Sonar exposes no route that sets a gain directly; the whole
config has to be PUT back, so applying a boost edits whichever preset is selected on that channel at the
time, permanently. Two things follow:

- Applied **on change only** — once set for a device, nothing is written again until something actually
  changes, so you stay free to move the slider by hand afterwards.
- Every preset carries its own boost, so switching presets replaces it. The trigger is therefore the
  *pair* — output device and selected preset — which means a preset switch re-applies the boost while a
  device that has not moved is left alone. Losing and regaining Sonar re-arms it too.

Off by default, and classic mode only — streamer mode redirects through different routes, so the app
says so in the log and stands aside rather than editing a preset based on the wrong device.

### Restoring a boost

On its own the feature never takes a boost back off: unticking it, or moving the channel to a device
with no rule, leaves the last boost sitting in the preset. Tick **"Restore to _n_ dB when a device stops
using it"** to have those presets put back instead. 0 dB is Sonar's own default.

Three things trigger a restore: the feature being switched off, the rule being deleted or disabled, and
the channel moving to a device with no rule.

Only presets this app actually boosted are touched — the ids are recorded in `boostedConfigs`, so a
Volume Boost set by hand in GG is never reset. That record is persisted, so unticking the feature
tomorrow still undoes what was applied today, and a restore that cannot reach Sonar keeps its record and
retries rather than forgetting what it changed.

## Default ChatMix

Sonar's **ChatMix** balances the game and chat audio streams — centred is full volume of both, and
moving towards one end turns the other down. It normally lives on a dial on the headset, and GG has no
setting for where it should start, so a GG restart or a stray bump of the dial leaves it wherever it
landed.

**Default ChatMix** puts your balance back **once, each time the app connects to Sonar** — at startup,
and again after a GG restart. It is deliberately not held: turn the dial afterwards and it stays where
you put it until the next reconnect. Holding it would mean fighting the hardware dial every few seconds,
which is the opposite of what the dial is for.

The slider runs from full Game to full Chat. `ChatMixService` polls `/v1/chatMix` on its own, and the
latch that stops it re-writing is cleared the moment a poll fails — that single fact is what makes the
value come back after GG restarts. Moving the slider re-arms it immediately rather than waiting for the
next reconnect.

Off by default. ChatMix only exists for sources with separate game and chat audio inputs; where Sonar
reports it as unavailable the control greys out and says which state it reported.

## Settings

![The Settings tab: default preset, default output device, hold Windows default, and the poll interval](docs/screenshots/settings.png)

Everything on this tab is saved as it is changed, to
`%APPDATA%\SonarPresetSwitcher\settings.json`, written atomically. Holds the mappings, the default
preset, the held default device, the per-device volume boosts, tray/autostart toggles, and the poll
interval.

## Caveat

The Sonar API is undocumented and private to SteelSeries; a GG update could change it. All of that
surface is confined to `Services/SonarDiscovery.cs` and `Services/SonarClient.cs`, and the app degrades
to a "Sonar not running" state rather than crashing.

## About this project

This app was fully coded with AI. WPF was chosen because it is the development environment the
developer is most familiar with.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Short version: there is no test project and no linter, so
verification is manual against a live GG — and `Services/Interop/` has traps worth reading about
before you touch it.

## License

[MIT](LICENSE).

Not affiliated with, endorsed by, or supported by SteelSeries. "SteelSeries", "GG" and "Sonar" are
trademarks of their respective owner.
