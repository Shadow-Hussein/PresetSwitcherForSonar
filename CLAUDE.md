# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`README.md` documents the Sonar REST protocol, the discovery chain, and the preset-switching rules —
read it before touching `Services/Sonar*.cs`. This file covers what the README does not: how the parts
are wired together, and the traps that only show up at runtime.

## Commands

```bash
dotnet build "SonarPresetSwitcher.sln" -c Debug
```

Output: `src/SonarPresetSwitcher/bin/Debug/net10.0-windows/SonarPresetSwitcher.exe`. Pass `--minimized`
to start straight to the tray (what the "Start with Windows" toggle registers).

There is **no test project and no linter** — one WPF app project, zero NuGet dependencies, BCL only.
Verification is manual: run the app against a live SteelSeries GG. Do not go looking for a test runner.

Only one instance can run: a `Local\SonarPresetSwitcher.SingleInstance` mutex makes a second launch
exit silently with code 0. When a launch appears to "do nothing", kill strays first — and note that
running via `dotnet SonarPresetSwitcher.dll` leaves a process named `dotnet`, not `SonarPresetSwitcher`.

Release publish is single-file, self-contained, **win-x86** (`Properties/PublishProfiles/FolderProfile.pubxml`,
which publishes to a hard-coded Desktop path). Debug builds run AnyCPU, so they are x64 — any interop
must work under both.

## Architecture

Hand-wired in `App.OnStartup`; there is no DI container. The dependency order is:

```
SonarDiscovery -> SonarClient ─┐
ProcessWatcher ────────────────┼-> PresetCoordinator -> MainViewModel -> MainWindow
      │            AudioRoutingService ─┘              │             └-> TrayIconHost
      └───────────────> DefaultDeviceGuard ────────────┘
AudioDeviceService ─────> both audio services
```

`ProcessWatcher` feeds `DefaultDeviceGuard` too: it reports *every* process name, not just mapped ones
(the mapped-name filter lives in `PresetCoordinator`), so a `SteelSeries*` process appearing is the
guard's cue to check the default device more often for a minute.

**`PresetCoordinator` is the decision layer** and the only caller of `SonarClient.SelectAsync`. It owns
`_runningStack` (mapped processes, oldest first, last one wins) and funnels everything through
`ApplyTargetAsync` behind a `SemaphoreSlim`. It exposes three outward signals, and the distinction
matters:

- `StateChanged` — connection/preset/active-mapping, consumed by both the VM and the tray.
- `Activity` — human-readable log lines, including connects, skips and errors.
- `PresetSwitched` — fires **only** on a successful switch. Deliberately separate from `Activity` so
  that connects and errors do not become desktop notifications.

**`AppSettings` is a single mutable instance shared by reference** between the coordinator and the view
model. Every write goes through `MainViewModel.Persist()` → `SettingsStore.Save` + `coordinator.UpdateSettings`.
To add a setting: add a `[JsonPropertyName]` property to `AppSettings`, then a VM property whose setter
calls `Persist()`. Computed properties need `[JsonIgnore]` or they leak into settings.json.

Every activity line is mirrored to `%APPDATA%\SonarPresetSwitcher\activity.log` by `ActivityFileLog`,
called from the single `MainViewModel.Log` funnel. This is the only record of what happened at logon:
`MainWindow.Loaded` never fires under `--minimized`, so `InitializeAsync` does not run and the in-window
log starts empty. Read it before theorising about a startup bug.

Settings live at `%APPDATA%\SonarPresetSwitcher\settings.json` (atomic write via `.tmp` + `File.Move`).
Missing keys fall through to property initializers, which is the migration story — there is no explicit
migration code.

`ProcessWatcher` tracks process **names only**, never PIDs; anything needing a PID resolves it at the
point of use with `Process.GetProcessesByName`.

## Audio interop (`Services/Interop/`)

Two separate undocumented APIs, and the distinction matters:

- **`AudioPolicyConfig.cs`** — the *persisted per-application default endpoint*, the same thing
  Settings → Sound → Volume mixer writes. Used by `AudioRoutingService`. Never touches the system
  default device.
- **`PolicyConfig.cs`** — the *system* default endpoint, the same thing "Set as Default Device" in the
  Sound control panel writes. Used only by `DefaultDeviceGuard`, only when the user has ticked "Hold
  Windows default", and only for the Console and Multimedia roles (Communications is left to Sonar's
  Chat device). This exists because SteelSeries GG re-claims the default for "Sonar - Gaming" at every
  startup and offers no way to disable that.

Read the class comment in `AudioPolicyConfig.cs` before changing anything there; it records a layout
established empirically, not from documentation.

Constraints that are easy to rediscover the hard way:

- `ComInterfaceType.InterfaceIsIInspectable` and `UnmanagedType.HString` **throw on .NET** (built-in
  WinRT interop was removed in .NET 5). Published C# implementations of this API rely on both, so their
  interface declarations omit IInspectable's three vtable slots — ours must declare them explicitly and
  marshal HSTRINGs by hand. `IntPtr.Zero` is a valid empty HSTRING and is how an override is cleared.
- Which interface variant the OS exposes is decided by **QueryInterface, never a build-number check**.
  The IIDs differ, so a wrong guess fails the cast instead of calling into an arbitrary vtable slot.
- **Never probe unknown vtable slots on a live machine.** One of `IAudioPolicyConfig`'s neighbouring
  slots is `ClearAllPersistedApplicationDefaultEndpoints`, which wipes every per-app output override
  system-wide; `IPolicyConfig`'s neighbours include `SetEndpointVisibility`, which can hide an endpoint.
  `AudioPolicyConfig` validates its layout with a read-only probe of `GetPersistedDefaultAudioEndpoint`;
  `PolicyConfig` has no readable equivalent, so `DefaultDeviceGuard` reads the default endpoint back
  after every write instead. **That read-back is proof of success, never of failure.** The guard stands
  down only when Windows reported success for every role and the default nevertheless never moved — the
  signature of a wrong slot. A default that moves and is moved back is a fight with another app, and the
  guard keeps fighting; once a write has been seen to land, `_apiProven` makes standing down impossible.
  Collapsing those two cases into one is what used to kill the feature at every logon.
- Windows returns `E_INVALIDARG` for any process without an audio session. A game is detected the moment
  its process appears, seconds before it initialises audio, so the first attempt fails routinely —
  `AudioRoutingService` retries with backoff. Treat `E_INVALIDARG` as "not yet", not "broken".
- The override is persistent and keyed by executable: it outlives the process and applies to the next
  launch. `AudioRoutingService.Forget` drops only the cached state, deliberately leaving the override.
- `DefaultDeviceGuard` polls rather than registering an `IMMNotificationClient`. Every other COM call
  here creates and releases an enumerator inside a single call; a long-lived callback would drag
  apartment and lifetime problems into a service that otherwise has none. Being wrong for a second at
  boot costs nothing — as long as it is recoverable, which is the whole point of the design.
- **Nothing about the audio stack may be decided at t=0 and cached.** `CPolicyConfigClient` can fail to
  create in the first seconds of a logon, so `AudioDeviceService.EnsureDefaultDeviceSupport` retries on a
  backoff instead of a `Lazy`, and `CanSetDefaultDevice` is a non-forcing read of what it last found.
  `EnsureDefaultDeviceSupport` is also the only caller of `PolicyConfig.Create`, and it runs on the
  guard's timer thread on purpose: activating that class from the WPF STA proxies every later write
  through the UI thread's message queue, which fails under logon load.
- The guard's poll interval is computed each tick (`ApplyCadence`), never latched: 1s during the first
  minute and after a `SteelSeries*` process appears, 15s while another app is fighting it, 2s otherwise —
  and it recovers when the fight goes quiet.

## WPF specifics

**The WinForms implicit `using` is removed** (`<Using Remove="System.Windows.Forms" />`) so `Application`
and `MessageBox` resolve to WPF. Any new file touching WinForms types needs an explicit
`using System.Windows.Forms;` **and** `using Application = System.Windows.Application;` — the pattern at
the top of `Views/TrayIconHost.cs`.

**All palette and control styling lives in `Views/Theme.xaml`**, merged into `Application.Resources`.
Neither window defines its own brushes or styles. Buttons and combo boxes carry full `ControlTemplate`s
because stock WPF chrome ignores `Background`/`BorderBrush` triggers. A missing key here surfaces as a
`XamlParseException` at window construction, not at build time.

Sonar ships ~330 Game presets, so preset combo boxes need both `VirtualizingStackPanel` (set in the
`ComboBox` template) and `TextSearch.TextPath="Name"` — the latter because `ItemTemplate` replaced
`DisplayMemberPath`, and without it type-to-jump silently stops working.

**Assets must load via pack URI, never a file path** (`pack://application:,,,/Assets/app.ico`), because
the release publish is single-file — no loose `Assets` folder sits next to the exe. `app.ico` is an
`<Resource>`, embedded in the assembly.

`Assets/app.ico` is generated, not hand-drawn. It carries 8 sizes; frames below 128px must be classic
DIB, not PNG, because `System.Drawing.Icon` on .NET Framework cannot decode a PNG-compressed 16px frame.

## Verification

The app degrades rather than crashes when Sonar is absent, so "it launched" proves little. Worth
checking against a live GG: preset ordering (favourites first, then alphabetical), a real switch
producing a toast, and — for audio routing — that Settings → Sound → Volume mixer shows the game on the
chosen device while the *system* default is unchanged.
