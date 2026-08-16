# Sonar Gaming Preset Switcher

[![Build](https://github.com/Shadow-Hussein/SonarGamingPresetSwitcher/actions/workflows/build.yml/badge.svg)](https://github.com/Shadow-Hussein/SonarGamingPresetSwitcher/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform: Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4)
![.NET 10](https://img.shields.io/badge/.NET-10.0--windows-512BD4)

A small WPF tray app that automatically switches the **SteelSeries Sonar Game-channel preset** when a
game launches, and restores a default preset when no mapped game is running.

## Install

Grab the latest zip from [Releases](https://github.com/Shadow-Hussein/SonarGamingPresetSwitcher/releases),
extract it anywhere, and run `SonarPresetSwitcher.exe`. The release build is self-contained, so no .NET
runtime install is needed. To build from source instead, see below.

## Build & run

```bash
dotnet build "SonarGamingPresetSwitcher.sln"
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
| `GET` | `/mode` | liveness probe |

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

## Settings

`%APPDATA%\SonarPresetSwitcher\settings.json`, written atomically. Holds the mappings, the default
preset, the held default device, tray/autostart toggles, and the poll interval.

## Caveat

The Sonar API is undocumented and private to SteelSeries; a GG update could change it. All of that
surface is confined to `Services/SonarDiscovery.cs` and `Services/SonarClient.cs`, and the app degrades
to a "Sonar not running" state rather than crashing.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Short version: there is no test project and no linter, so
verification is manual against a live GG — and `Services/Interop/` has traps worth reading about
before you touch it.

## License

[MIT](LICENSE).

Not affiliated with, endorsed by, or supported by SteelSeries. "SteelSeries", "GG" and "Sonar" are
trademarks of their respective owner.
