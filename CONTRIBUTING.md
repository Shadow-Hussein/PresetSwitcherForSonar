# Contributing

Thanks for taking a look. This is a small, single-project WPF app — the setup is deliberately plain.

## Getting set up

You need the **.NET 10 SDK** and, for anything beyond a compile check, **SteelSeries GG** running
with the Sonar sub-app enabled.

```bash
dotnet build "SonarPresetSwitcher.sln" -c Debug
```

The executable lands in `src/SonarPresetSwitcher/bin/Debug/net10.0-windows/SonarPresetSwitcher.exe`.
Pass `--minimized` to start straight to the tray.

There are **no NuGet dependencies** (BCL only) and please keep it that way unless there is a strong
reason — the release build is single-file and self-contained, and every dependency is weight in it.

## Verification

**There is no test project and no linter.** Verification is manual. "It compiled" and "it launched"
prove very little here, because the app is written to degrade rather than crash when Sonar is absent.

Before opening a PR, run the app against a live GG and confirm the parts you touched actually work.
The PR template lists what to check; `CLAUDE.md` explains why each item is on the list.

A single instance is enforced by a `Local\SonarPresetSwitcher.SingleInstance` mutex — a second launch
exits silently with code 0. If a launch appears to do nothing, kill stray processes first.

## Things that will bite you

`README.md` documents the Sonar REST protocol and the switching rules. `CLAUDE.md` documents how the
pieces are wired together and the runtime traps. Read both before changing:

- **`Services/Sonar*.cs`** — Sonar's port changes on every GG launch and is discovered at runtime.
- **`Services/Interop/`** — hand-written COM interop for Windows' per-app audio endpoint API, with a
  vtable layout established empirically rather than from documentation. One neighbouring slot wipes
  every per-app audio override on the machine. Read the class comment in `AudioPolicyConfig.cs`
  first, and never probe unknown slots on a live machine.
- **`Views/Theme.xaml`** — all palette and control styling lives here. A missing resource key shows
  up as a `XamlParseException` at window construction, not at build time.

## Style

`.editorconfig` covers the mechanics. Beyond that: match the surrounding code. Comments in this
codebase explain *why* something non-obvious is the way it is — they are not narration of what the
next line does. Keep that ratio.

## Commits and PRs

- Branch off `main`; keep one logical change per PR.
- Present-tense, imperative commit subjects (`Fix preset fallback when GG restarts`).
- Say in the PR description how you verified it. "Builds clean" is not verification.

## Reporting bugs

Open an issue with the bug template. The **GG version** is usually the decisive detail: the Sonar API
is undocumented and private to SteelSeries, so a GG update can break discovery or switching outright.
