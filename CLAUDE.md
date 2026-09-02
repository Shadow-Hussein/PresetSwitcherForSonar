# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`README.md` documents the Sonar REST protocol, the discovery chain, and the preset-switching rules —
read it before touching `Services/Sonar*.cs`. This file covers what the README does not: how the parts
are wired together, and the traps that only show up at runtime.

## Answering style

Keep plans under 500 words and chat responses under 200. Both cover the whole ask — cut restatement,
option surveys and recaps, not scope.

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
      └───────────────> DefaultDeviceGuard ────────────┤
AudioDeviceService ─────> both audio services          │
SonarClient ────────────> ChannelBoostService ─────────┤
SonarClient ────────────> ChatMixService ──────────────┘
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
- `SettingsChanged` — the coordinator wrote `AppSettings.ModeBeforeStreamer` and it needs saving. The
  VM handles it with `SettingsStore.Save` **only**, never the full `Persist()`: `Persist` calls
  `UpdateSettings`, which re-enters `ApplyTargetAsync`, which is where the event came from. Nothing
  else reads the field, so there is nothing to fan out.

`SonarConfig.Inherit` (`~global`) is a mapping that names no preset. `FindMapping` still returns it —
the id is non-blank, so it is a live mapping — and `namesPreset` is what falls back to the default in
`ApplyTargetAsync`. Resolving it any earlier would lose the winner, and with it the routing, the
recording device and the streamer-mode flag, which is the whole point of the option. It is a
`static readonly` singleton because `SonarConfig` is a class: the ComboBox matches `SelectedItem` by
reference, so a fresh instance per rebuild would silently deselect the row. `MainViewModel` keeps it in
`MappingPresets` only, never `Presets` — the Settings default picker binds to the latter, and a default
that inherits from itself is meaningless.

**Recording apps are tracked in `_runningRecorders`, never in `_runningStack`.** That stack decides
which preset wins; a `RecordingApp` names no preset, so letting one join it would let OBS beat the game
for the Game channel. It is a `HashSet` rather than a list because there is no winner to pick — every
running recorder gets its input applied, since two capture apps can want two different microphones.
`EnabledRecordingApps` is where the `ApplyRecordingApps` master toggle is enforced, once, so switching
it off hides recorders from `ApplyAudioRouting` *and* `ApplyStreamerModeAsync` together — the toggle
means "do nothing", not "do half of it".

It is also the only caller of `SonarClient.SetModeAsync`, via `ApplyStreamerModeAsync`. That belongs
here rather than in `ChannelBoostService` (which owns *reading* `/mode`) because it is keyed on running
processes, exactly like preset selection. Its traps:

- **`PUT /mode/{mode}` takes the value in the path, and there is no body.** The verb was established
  from `Allow: PUT` on the 405 that a `GET` on the same path returns — the route templates are not
  literal strings in `SoundStage.Api.dll`, so grepping for them finds nothing.
- **The streamer mode's wire value is `stream`. GG's UI says "Streamer"; the API does not.** Guessing
  `"streamer"` breaks two things at once and neither of them loudly: `PUT /mode/streamer` answers 400
  `{"error":"Request validation error"}` — so the switch is refused and only shows up as an activity
  line — and `GET /mode` returns `"stream"`, which then misses every `SonarMode.Is` check and lands in
  `ChannelBoostService`'s *unknown mode* branch, pausing the boost instead of following the Monitoring
  mix. Both were confirmed against a live GG. `SonarMode.Streamer` is the single source; never write
  the literal.
- **`ModeBeforeStreamer` is written only after Sonar accepts the switch, and that is the whole safety
  property.** A mode Sonar was *already* in is never recorded, so a user who lives in streamer mode is
  never dropped into classic by us. It is persisted for the same reason `BoostedConfigs` is: being
  killed mid-stream must not strand the mixer somewhere the user did not put it.
- **Edge-triggered, not held.** `ApplyTargetAsync` runs on process start/stop, on reconnect and on a
  settings edit — never on a timer — so switching the mode by hand in GG between those is left alone.
- The whole thing costs no HTTP at all when no mapping wants streamer mode and nothing is outstanding,
  which is the common case; the `GetModeAsync` call sits *after* that check for exactly that reason.
- A failed restore **keeps** the record and retries on the next apply, rather than forgetting.

**`ChannelBoostService` is a second, independent Sonar poller** and the only caller of
`SonarClient.PutConfigAsync`. It is deliberately *not* part of `PresetCoordinator`: that class is keyed
on running processes and owns preset selection, while the boost is keyed on Sonar's redirection state.
Traps specific to it:

- **Sonar's "Volume boost" is `data.generalGain`** on a config — 0 to 12 dB in 0.1 steps (confirmed from
  GG's own frontend: `minValue: 0, maxValue: 12, step: .1, unit: "dB"`). There is **no route that sets a
  gain directly**; every route template in `SoundStage.Api.dll` was dumped and none mentions gain or
  boost. The only write path is `PUT /configs` with the entire config body.
- **Because of that, configs are round-tripped as `JsonObject`, never as `SonarConfig`.** The `data`
  payload carries a ten-band parametric EQ, virtual surround positions and more; deserialising into a
  typed model and PUTting it back would silently drop everything unmodelled. Read the node, change the
  one field, send the node.
- **The boost belongs to the preset, not the device**, so applying one permanently edits whichever
  preset is selected at the time. That is a deliberate, user-approved trade-off, not an oversight.
- **On-change-only is the whole design, not an optimisation**, and the key is the *pair*: `_applied`
  maps channel → `(DeviceId, ConfigId)`. Keying on the device alone would fail to re-apply after a
  preset switch (each preset carries its own gain); re-writing every tick would revert the user's own
  slider. `UpdateSettings` sets a `volatile` flag rather than clearing `_applied` directly, so that
  dictionary stays owned by the loop thread alone.
- `/configs/selected` is only fetched when a rule actually matches the current device — it carries the
  full EQ payload for all five channels, so pulling it every tick would be wasteful.
- **Both modes are handled, and `ResolveDevicesAsync` is the whole of the difference.** It reduces either
  routing table to one `channel -> deviceId` map — "the endpoint the user hears this on" — and the rest
  of the tick is mode-blind. Classic reads `/classicRedirections`. Streamer reads
  `/streamRedirections`, whose `monitoring` entry carries the analogous `deviceId` plus a per-role
  `status` array; **only `monitoring` is matched, never `streaming`**, because a rule means "the level I
  want on the thing I am listening on", and matching both would let two rules fight over the one gain a
  preset has. `/streamRedirections/isStreamMonitoringEnabled` gates it: monitoring off means nothing to
  follow, so the boost is released rather than held.
- **`/streamRedirections` answers 200 in classic mode too**, with blank `deviceId`s — it is not a mode
  probe, and a blank `deviceId` is also the normal state until streamer mode has been set up with an
  output. Both cases resolve to an empty map, which releases rather than misfires.
- **An unrecognised mode is its own case, and it returns null rather than an empty map.** Empty means
  "connected, nothing matches" and lets `ReleaseAsync` run; null holds everything including the
  `BoostedConfigs` records, because a routing table we cannot read is not evidence that a boost is no
  longer wanted. Anything that is not `classic` or `stream` lands here and is logged verbatim.
- **`/mode` is fetched before the feature-disabled early return**, not after, because the status card's
  mode chip depends on it whether or not the boost is switched on — the same reason the ChatMix poll
  keeps running while its feature is off. `RaiseRedirections` moved above that return too, so the "in
  use" dots are live while rules are still being set up, which is exactly when they are useful. The tick
  still stops right after the mode read when there are **no rules and nothing outstanding**: a user who
  never touches the feature should not be paying a routing GET every five seconds for it.
  `RefreshVirtualDevicesAsync` stays above that stop — it is once per connection, and the device picker
  needs it *before* the first rule exists.
- `_invalidated` clears `_lastRaised` as well as `_applied`. Without that, adding a rule for the device
  already in use raises nothing (the redirections have not moved), and the new row's marker never lights.
- **The announcement latch is a single `_announced` string, not a flag per condition.** Classic,
  streamer, streamer-with-monitoring-off and unknown are four states that transition between each other
  freely; one latch keyed on the state makes exactly one line per transition unavoidable. Starting up in
  classic says nothing (the ordinary case), and `MarkUnreachable` resets it so a reconnect re-states.
- Not every config has a `generalGain`: the `chatRender` and `chatCapture` ones carry a completely
  different key set. `ReadGeneralGain` returning null is normal, not an error.
- **`BoostedConfigs` is what makes the restore option safe, and it is persisted for that reason.** It
  records the preset ids this app actually wrote a boost into, so releasing one never resets a boost the
  *user* set by hand — there is no other way to tell the two apart. It is only added to after a PUT
  succeeds, never on the epsilon-skip path, or a preset that already happened to sit at the target would
  be claimed and later zeroed. An in-memory list would defeat the point: the whole scenario is unticking
  the feature days later.
- The release path is the one place that fetches `/configs` rather than `/configs/selected` — a preset
  switch leaves our boost on the preset we switched away from, which the selected list no longer
  carries. If that fetch fails the records are **kept** and retried, never dropped.
- `SettingsChanged` is raised once at the end of a tick, not per edit, and the VM handles it with
  `Dispatch(Persist)`. The service mutates `AppSettings.BoostedConfigs` on its own thread while the UI
  thread serialises it; firing once, after all mutation for that tick is done, is what keeps those two
  off each other.
- Sonar's own virtual endpoints are filtered out of **the boost picker only** (`isVad` from
  `/audioDevices`, with `AudioDevice.LooksLikeSonarVirtual` as the offline fallback). The other pickers
  keep them on purpose — routing an app *to* "Sonar - Gaming", or holding it as the Windows default, are
  both legitimate.

**`ChatMixService` is a third independent poller** and the only caller of `SonarClient.SetChatMixAsync`.
It is keyed on nothing but the connection, which is why it is neither part of `PresetCoordinator` (keyed
on running processes) nor of `ChannelBoostService` (keyed on Sonar's redirection state). Its traps:

- **`/v1/chatMix` is the one `/v1`-prefixed route in the app.** `/chatMix` is a 404.
- **The balance binds from the query string, not the body.** `PUT /v1/chatMix?balance=0.4` works;
  the same value as a JSON body answers **200 and writes 0**, because the parameter falls back to its
  default. A body here is a silent wrong value, not an error — this was confirmed against a live GG, so
  do not "make it consistent" with `PutConfigAsync`.
- Range is -1 to 1 and Sonar answers 400 outside it. `-1` is full game, `1` is full chat; that sign
  convention is **not** derivable from the API — the resulting gains are applied below
  `/volumeSettings/classic`, whose numbers do not move with the balance.
- **Values do not round-trip.** Sonar stores the balance as a float, so 0.75 reads back as 0.75000006
  and 0.35 as 0.34999996. `BalanceEpsilon` exists for that, not as an optimisation.
- **Apply-once is the entire design, and `_applied = false` on a failed poll is the whole mechanism.**
  The write happens on the transition into "reachable" and never again until that transition repeats, so
  the headset dial stays usable. Holding the value instead would mean fighting the hardware every five
  seconds. `UpdateSettings` sets `_invalidated` so a slider move takes effect now rather than at the
  next reconnect.
- A rejected value latches too, so a balance Sonar refuses is not retried every tick.
- Nothing is persisted back — unlike the boost, this writes transient state on Sonar rather than editing
  a preset, so there is no `BoostedConfigs` analogue and nothing to restore on release.
- `state` is the availability gate and its full value set is undocumented; only `"enabled"` is treated as
  usable, and anything else is logged verbatim, once per transition.
- The poll runs even with the feature switched off, so the UI can grey the control out on a machine
  without ChatMix hardware before the user ever ticks the box.

The `Slider` in `Views/Theme.xaml` is declared low in the dictionary on purpose: it reuses
`ScrollPageButton` for the click-in-the-track areas, and `StaticResource` only sees keys above it. Its
0 marker is a drawn `Rectangle`, not a `TickBar` — `TickFrequency` is 0.01, which would paint two
hundred ticks. The XAML binds it with `Delay=400`; without that, every pixel of a drag runs `Persist()`.

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
  default device. It serves **both data flows**: render for a mapping's output device, capture for its
  recording device. The device path is built from the interface GUID matching the flow
  (`DEVINTERFACE_AUDIO_RENDER` vs `DEVINTERFACE_AUDIO_CAPTURE`) — a capture endpoint addressed with the
  render GUID is rejected, not mis-routed. Both flows write Console and Multimedia and leave
  Communications alone, matching what the mixer's own pickers do.
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
- **`AudioRoutingService` tracks `_applied` and `_retries` on the (process, direction) *pair*, not the
  process.** The two directions are accepted independently by Windows, and a recording app opens its
  capture session on a different schedule from its render one, so one shared retry loop would let a
  successful output push cancel an input that had not landed yet.
- **`AudioDevice.UnmanagedId` ("Don't change") and `SystemDefaultId` ("") are not the same thing.**
  Empty *clears* the override; unmanaged means never call at all. `AudioRoutingService.ApplyInput`
  returns early on it rather than pushing a clear. `WithMissing`, `Repair` and `SyncInputDevice` all
  have to skip it, and `SyncInputDevice` falls back to it rather than to the system default — falling
  back to "" would turn a missing device into an instruction to clear.
- **`MainViewModel.DefaultRecordingDevice` runs on add only, never on sync**, which is what keeps it
  from overwriting a device the user picked. It searches for a name containing "Stream" rather than
  naming a constant: Sonar creates that endpoint only once streamer mode has been set up with an
  output, so on a machine that has never been in streamer mode it does not exist and the search falls
  through to the Sonar virtual mic. The day GG creates it, new rows pick it up with no code change.
- **Render and capture ids live in separate namespaces**, so `RepairSavedDeviceReferences` is
  parameterised over the pool. Repairing an input reference against the render list matches nothing, or
  worse, a playback endpoint that happens to share a friendly name.
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
