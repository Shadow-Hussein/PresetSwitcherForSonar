using System.Diagnostics;
using PresetSwitcherForSonar.Models;

namespace PresetSwitcherForSonar.Services;

public sealed record CoordinatorState(
    bool Connected,
    string? Endpoint,
    string? CurrentPresetName,
    string? ActiveMappingName);

/// <summary>A preset switch that actually happened, for the tray notification.</summary>
/// <param name="GameName">Null when reverting because no mapped game is running.</param>
/// <param name="OutputDeviceName">Null when the game is on the Windows default device.</param>
public sealed record PresetSwitchNotice(string PresetName, string? GameName, string? OutputDeviceName);

/// <summary>
/// The decision layer. It owns the stack of currently running mapped games and is the only thing in
/// the app that calls <see cref="SonarClient.SelectAsync"/>.
///
/// Rules:
///   - Most recently started mapped game wins.
///   - When it exits, fall back to the next mapped game still running, then to the default preset.
///   - A switch is only issued when the target differs from the preset Sonar reports as selected, so
///     a preset the user picks by hand in GG mid-game is not fought over.
///
/// Audio routing rides along here because this class already knows which mapped games are running,
/// but it is applied independently of the Sonar switch: the device has to be set even when the preset
/// is already correct.
/// </summary>
public sealed class PresetCoordinator : IDisposable
{
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(10);

    private readonly SonarClient _client;
    private readonly ProcessWatcher _watcher;
    private readonly AudioRoutingService _routing;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    /// <summary>Running mapped processes, oldest first. The last entry is the winner.</summary>
    private readonly List<string> _runningStack = [];

    /// <summary>
    /// Running processes matching an enabled <see cref="RecordingApp"/>. Kept out of
    /// <see cref="_runningStack"/> on purpose: that stack decides which preset wins, and a recording
    /// app names no preset — letting one join it would make OBS beat the game for the Game channel.
    /// </summary>
    private readonly HashSet<string> _runningRecorders = new(StringComparer.OrdinalIgnoreCase);

    private AppSettings _settings;
    private IReadOnlyList<SonarConfig> _presets = [];
    private string? _lastKnownSelectedId;
    private Task? _statusLoop;

    public PresetCoordinator(SonarClient client, ProcessWatcher watcher, AudioRoutingService routing, AppSettings settings)
    {
        _client = client;
        _watcher = watcher;
        _routing = routing;
        _settings = settings;

        _watcher.Started += OnProcessesStarted;
        _watcher.Stopped += OnProcessesStopped;
    }

    /// <summary>Fired whenever connection state, the current preset, or the active mapping changes.</summary>
    public event Action<CoordinatorState>? StateChanged;

    /// <summary>Fired with a short human-readable line for the activity log.</summary>
    public event Action<string>? Activity;

    /// <summary>
    /// Fired only when a preset was actually switched. Deliberately separate from
    /// <see cref="Activity"/>, which also carries connects, skips and errors — those must not become
    /// desktop notifications.
    /// </summary>
    public event Action<PresetSwitchNotice>? PresetSwitched;

    /// <summary>
    /// Raised when this class has changed <see cref="AppSettings.ModeBeforeStreamer"/> and it needs
    /// writing to disk.
    ///
    /// Handled by saving only, never by the view model's full <c>Persist()</c>: that would call back
    /// into <see cref="UpdateSettings"/> and re-enter <see cref="ApplyTargetAsync"/>. Nothing else reads
    /// the field, so there is nothing to fan out to.
    /// </summary>
    public event Action? SettingsChanged;

    /// <summary>Game-channel presets as of the last refresh, favourites first.</summary>
    public IReadOnlyList<SonarConfig> Presets => _presets;

    public bool Connected { get; private set; }

    public string? CurrentPresetName { get; private set; }

    public string? ActiveMappingName { get; private set; }

    public void Start()
    {
        _statusLoop ??= Task.Run(() => StatusLoopAsync(_cts.Token));
    }

    /// <summary>Called when the user edits mappings or settings in the UI.</summary>
    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        _watcher.IntervalSeconds = settings.ClampedPollInterval;

        // A mapping may have been added for a game that is already running, or removed for one that is.
        RebuildRunningStack();
        _ = ApplyTargetAsync("settings changed");
    }

    /// <summary>Re-reads the preset list from Sonar.</summary>
    public async Task<IReadOnlyList<SonarConfig>> RefreshPresetsAsync(CancellationToken ct = default)
    {
        var presets = await _client.GetGamePresetsAsync(ct).ConfigureAwait(false);
        if (presets.Count > 0)
            _presets = presets;

        return _presets;
    }

    private void OnProcessesStarted(IReadOnlyCollection<string> names)
    {
        var matched = new List<string>();
        foreach (var name in names)
        {
            var key = GameMapping.NormalizeProcessName(name);
            var isMapped = FindMapping(key) is not null;
            var isRecorder = FindRecordingApp(key) is not null;

            if (!isMapped && !isRecorder)
                continue;

            if (isMapped && !_runningStack.Contains(key, StringComparer.OrdinalIgnoreCase))
                _runningStack.Add(key);

            if (isRecorder)
                _runningRecorders.Add(key);

            matched.Add(key);
        }

        if (matched.Count == 0)
            return;

        _ = ApplyTargetAsync($"{string.Join(", ", matched)} started");
    }

    private void OnProcessesStopped(IReadOnlyCollection<string> names)
    {
        var removed = false;
        foreach (var name in names)
        {
            var key = GameMapping.NormalizeProcessName(name);
            var wasMapped = _runningStack.RemoveAll(p =>
                string.Equals(p, key, StringComparison.OrdinalIgnoreCase)) > 0;

            if (!wasMapped && !_runningRecorders.Remove(key))
                continue;

            _runningRecorders.Remove(key);
            removed = true;

            // Let the next launch re-apply its devices; the override Windows keeps is per-exe.
            _routing.Forget(key);
        }

        if (!removed)
            return;

        _ = ApplyTargetAsync("mapped app exited");
    }

    private void RebuildRunningStack()
    {
        var running = _watcher.RunningProcesses;

        // Drop entries that are no longer running or no longer mapped, keeping start order intact.
        _runningStack.RemoveAll(p => !running.Contains(p) || FindMapping(p) is null);

        // Pick up mapped games that were already running before the mapping existed.
        foreach (var mapping in _settings.Mappings.Where(m => m.Enabled))
        {
            var key = GameMapping.NormalizeProcessName(mapping.ProcessName);
            if (running.Contains(key) && !_runningStack.Contains(key, StringComparer.OrdinalIgnoreCase))
                _runningStack.Add(key);
        }

        // Recorders are a set, not a stack: every running one is applied, and there is no winner to
        // pick, so start order carries no meaning here.
        _runningRecorders.Clear();
        foreach (var app in EnabledRecordingApps())
        {
            var key = GameMapping.NormalizeProcessName(app.ProcessName);
            if (running.Contains(key))
                _runningRecorders.Add(key);
        }
    }

    /// <summary>
    /// The recording apps the feature is allowed to act on. The master toggle is checked here rather
    /// than at each call site, so switching it off makes every recorder invisible to this class at once
    /// — including to <see cref="ApplyStreamerModeAsync"/>, which is what makes the toggle mean
    /// "do nothing" rather than "do half of it".
    /// </summary>
    private IEnumerable<RecordingApp> EnabledRecordingApps() =>
        _settings.ApplyRecordingApps
            ? _settings.RecordingApps.Where(a => a.Enabled)
            : [];

    private RecordingApp? FindRecordingApp(string processKey) =>
        EnabledRecordingApps().FirstOrDefault(a =>
            string.Equals(
                GameMapping.NormalizeProcessName(a.ProcessName), processKey,
                StringComparison.OrdinalIgnoreCase));

    private GameMapping? FindMapping(string processKey) =>
        _settings.Mappings.FirstOrDefault(m =>
            m.Enabled &&
            !string.IsNullOrWhiteSpace(m.PresetId) &&
            string.Equals(GameMapping.NormalizeProcessName(m.ProcessName), processKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>Works out the preset that should be active right now and applies it if it differs.</summary>
    private async Task ApplyTargetAsync(string reason)
    {
        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            GameMapping? winner = null;
            for (var i = _runningStack.Count - 1; i >= 0 && winner is null; i--)
                winner = FindMapping(_runningStack[i]);

            // A mapping can deliberately name no preset (SonarConfig.Inherit). It still wins — its
            // output device, recording device and streamer-mode flag all apply — but the preset is
            // left to the global default, exactly as if nothing were running.
            var namesPreset = winner is not null && !SonarConfig.IsInherit(winner.PresetId);
            var targetId = namesPreset ? winner!.PresetId : _settings.DefaultPresetId;
            var targetName = namesPreset ? winner!.PresetName : _settings.DefaultPresetName;
            ActiveMappingName = winner?.DisplayName;

            // Independent of the Sonar switch below — the device still has to be set when the preset
            // is already correct, and when there is no preset configured at all.
            var deviceName = ApplyAudioRouting(winner);

            // Before the preset, and before the bail-out below: the mode governs how Sonar routes
            // everything, and a mapping can ask for it without naming a preset at all.
            await ApplyStreamerModeAsync().ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(targetId))
            {
                // No mapped game running and no default configured: leave Sonar alone.
                RaiseStateChanged();
                return;
            }

            // Refresh the ground truth before deciding — the user may have changed it in GG.
            var selected = await _client.GetSelectedGameConfigAsync(_cts.Token).ConfigureAwait(false);
            if (selected is not null)
            {
                _lastKnownSelectedId = selected.Id;
                CurrentPresetName = selected.Name;
            }

            if (string.Equals(_lastKnownSelectedId, targetId, StringComparison.OrdinalIgnoreCase))
            {
                RaiseStateChanged();
                return;
            }

            if (_presets.Count > 0 && _presets.All(p => !string.Equals(p.Id, targetId, StringComparison.OrdinalIgnoreCase)))
            {
                Activity?.Invoke($"Skipped: preset \"{targetName}\" no longer exists in Sonar.");
                RaiseStateChanged();
                return;
            }

            if (await _client.SelectAsync(targetId, _cts.Token).ConfigureAwait(false))
            {
                _lastKnownSelectedId = targetId;
                CurrentPresetName = targetName;
                Activity?.Invoke(namesPreset
                    ? $"Applied \"{targetName}\" for {winner!.DisplayName} ({reason})."
                    : winner is null
                        ? $"Reverted to \"{targetName}\" ({reason})."
                        : $"Applied the default \"{targetName}\" for {winner.DisplayName} ({reason}).");

                PresetSwitched?.Invoke(new PresetSwitchNotice(targetName, winner?.DisplayName, deviceName));
            }
            else
            {
                Activity?.Invoke($"Could not apply \"{targetName}\" — Sonar did not accept the request.");
            }

            RaiseStateChanged();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ApplyTarget failed: {ex}");
            Activity?.Invoke($"Error while switching preset: {ex.Message}");
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>
    /// Puts Sonar into streamer mode while any running <see cref="RecordingApp"/> asks for it, and back
    /// to the mode it was in once the last one has exited.
    ///
    /// <see cref="AppSettings.ModeBeforeStreamer"/> is the whole mechanism, and it is written **only
    /// after Sonar accepts the switch**. That is what makes the restore safe: a mode Sonar was already
    /// in when the app launched is never recorded, so a user who lives in streamer mode is never
    /// dropped back into classic by us. It is persisted for the same reason the boost records are — the
    /// app being killed while OBS is running must not strand the mixer in a mode the user did not pick.
    ///
    /// Edge-triggered, not held: this runs when a process starts or stops, on reconnect, and on a
    /// settings edit, so switching the mode by hand in GG between those is left alone.
    /// </summary>
    private async Task ApplyStreamerModeAsync()
    {
        var wanted = _runningRecorders.Any(key => FindRecordingApp(key) is { RequireStreamerMode: true });
        var remembered = _settings.ModeBeforeStreamer;

        // The overwhelmingly common case: nobody wants streamer mode and we have not moved it. Costs no
        // HTTP at all, so mappings that never use this feature never pay for it.
        if (!wanted && remembered.Length == 0)
            return;

        var mode = await _client.GetModeAsync(_cts.Token).ConfigureAwait(false);
        if (mode is null)
            return;

        if (wanted)
        {
            // Already there, whether we put it there or the user did. Either way, nothing to record:
            // if we did not move it, we have no business moving it back.
            if (SonarMode.Is(mode, SonarMode.Streamer))
                return;

            if (!await _client.SetModeAsync(SonarMode.Streamer, _cts.Token).ConfigureAwait(false))
            {
                Activity?.Invoke("Could not switch Sonar to streamer mode — Sonar refused the request.");
                return;
            }

            _settings.ModeBeforeStreamer = mode;
            SettingsChanged?.Invoke();
            Activity?.Invoke($"Switched Sonar to streamer mode (was {SonarMode.Describe(mode)}).");
            return;
        }

        // Nothing wants streamer mode any more and we have a mode to put back.
        if (SonarMode.Is(mode, remembered))
        {
            // Someone already put it back by hand. Drop the record rather than writing a mode change
            // that would do nothing.
            _settings.ModeBeforeStreamer = "";
            SettingsChanged?.Invoke();
            return;
        }

        if (!await _client.SetModeAsync(remembered, _cts.Token).ConfigureAwait(false))
        {
            // Keep the record and try again on the next apply, rather than leaving the user stranded.
            Activity?.Invoke(
                $"Could not switch Sonar back to {SonarMode.Describe(remembered)} mode — will retry.");
            return;
        }

        _settings.ModeBeforeStreamer = "";
        SettingsChanged?.Invoke();
        Activity?.Invoke($"Switched Sonar back to {SonarMode.Describe(remembered)} mode.");
    }

    /// <summary>
    /// Routes every running mapped game to its configured output device. All of them, not just the
    /// winner — two mapped games can be running and each wants its own device.
    /// </summary>
    /// <returns>The device in force for <paramref name="winner"/>, for the notification.</returns>
    private string? ApplyAudioRouting(GameMapping? winner)
    {
        string? winnerDevice = null;

        foreach (var key in _runningStack)
        {
            var mapping = FindMapping(key);
            if (mapping is null)
                continue;

            var result = _routing.Apply(mapping, _settings);
            if (ReferenceEquals(mapping, winner))
                winnerDevice = result.DeviceName;
        }

        // Recording apps are a separate feature and a separate list, so every running one is applied
        // rather than only a winner — two capture apps can want two different microphones.
        foreach (var key in _runningRecorders)
        {
            if (FindRecordingApp(key) is not { } app)
                continue;

            _routing.ApplyInput(app.ProcessName, app.DisplayName, app.InputDeviceId, app.InputDeviceName);
        }

        return winnerDevice;
    }

    /// <summary>
    /// Keeps the connection state and the displayed preset in sync. Sonar exposes no push channel, so
    /// this polls; it backs off to 10s while disconnected.
    /// </summary>
    private async Task StatusLoopAsync(CancellationToken ct)
    {
        var firstConnection = true;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var selected = await _client.GetSelectedGameConfigAsync(ct).ConfigureAwait(false);
                var connected = selected is not null;

                if (connected)
                {
                    _lastKnownSelectedId = selected!.Id;
                    CurrentPresetName = selected.Name;
                }

                var wasConnected = Connected;
                Connected = connected;

                if (connected && (firstConnection || !wasConnected))
                {
                    firstConnection = false;
                    await RefreshPresetsAsync(ct).ConfigureAwait(false);
                    Activity?.Invoke($"Connected to Sonar at {_client.Endpoint}.");

                    // Catch up: a mapped game may already be running.
                    RebuildRunningStack();
                    _ = ApplyTargetAsync("connected");
                }
                else if (!connected && wasConnected)
                {
                    Activity?.Invoke("Lost connection to Sonar.");
                }

                RaiseStateChanged();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Status loop failed: {ex}");
            }

            try
            {
                await Task.Delay(Connected ? StatusPollInterval : ReconnectInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void RaiseStateChanged() =>
        StateChanged?.Invoke(new CoordinatorState(Connected, _client.Endpoint, CurrentPresetName, ActiveMappingName));

    public void Dispose()
    {
        _watcher.Started -= OnProcessesStarted;
        _watcher.Stopped -= OnProcessesStopped;
        _cts.Cancel();
        _cts.Dispose();
        _applyGate.Dispose();
    }
}
