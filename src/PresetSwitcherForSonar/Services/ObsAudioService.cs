using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using PresetSwitcherForSonar.Models;

namespace PresetSwitcherForSonar.Services;

/// <summary>
/// Gives OBS one audio source per Sonar channel, each on its own recording track, and keeps those
/// sources pointed at Sonar's virtual devices.
///
/// The second half is the reason this is a service and not a one-off script: GG hands its virtual
/// endpoints fresh ids whenever it rebuilds its driver, and an OBS source holds the id, not the name —
/// so the source stays in the mixer and silently records nothing.
///
/// Two operations, and the split between them is deliberate:
/// <list type="bullet">
/// <item><see cref="SetupAsync"/>, the button: creates what is missing, adds it to the current scene,
/// and assigns tracks.</item>
/// <item>The repair, run once each time OBS starts: re-points sources that already exist and does
/// nothing else. It never creates a source and never touches tracks, so a source the user deleted, or
/// a track layout they changed, stays the way they left it.</item>
/// </list>
///
/// Neither deletes or renames anything, and neither writes OBS's profile — which tracks a recording
/// actually carries is the user's output setting, so it is read and reported, not changed.
///
/// Keyed on OBS starting, not on a timer: no socket is opened and Sonar is asked nothing while the
/// feature is off or OBS is not running.
/// </summary>
public sealed class ObsAudioService : IDisposable
{
    private const string RenderKind = "wasapi_output_capture";
    private const string CaptureKind = "wasapi_input_capture";
    private const int TrackCount = 6;

    /// <summary>The track every source is also on: the full mix, which is what a stream carries.</summary>
    private const int MixTrack = 1;

    /// <summary>
    /// Source name, the <c>role</c> and <c>dataFlow</c> <c>/audioDevices</c> reports for the endpoint,
    /// the OBS input kind, and the channel's own track. The flow is part of the key: Sonar exposes a
    /// <i>render</i> endpoint in the <c>chatCapture</c> role as well as the microphone itself.
    /// </summary>
    private static readonly Channel[] Channels =
    [
        new("Sonar Game", "game", "render", RenderKind, 2),
        new("Sonar Chat", "chatRender", "render", RenderKind, 3),
        new("Sonar Media", "media", "render", RenderKind, 4),
        new("Sonar Aux", "aux", "render", RenderKind, 5),
        new("Sonar Mic", "chatCapture", "capture", CaptureKind, 6),
    ];

    private static readonly string[] ObsProcesses = ["obs64", "obs32", "obs"];

    /// <summary>
    /// How long after OBS's process appears each repair attempt waits. Its WebSocket server opens a
    /// few seconds in, and answers NotReady until the scene collection has loaded.
    /// </summary>
    private static readonly TimeSpan[] RepairBackoff =
    [
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30),
    ];

    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);

    private readonly SonarClient _client;
    private readonly ProcessWatcher _watcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile bool _enabled;
    private bool _started;

    /// <summary>Latch, so a repair that fails the same way at every OBS start is logged once.</summary>
    private string? _lastProblem;

    public ObsAudioService(SonarClient client, ProcessWatcher watcher, AppSettings settings)
    {
        _client = client;
        _watcher = watcher;
        _enabled = settings.ApplyObsAudio;
    }

    /// <summary>Human-readable lines for the activity log.</summary>
    public event Action<string>? Activity;

    public void Start()
    {
        if (_started)
            return;

        _started = true;

        // The watcher's first tick reports everything already running, so an OBS that was open
        // before this app is repaired too.
        _watcher.Started += OnProcessesStarted;
    }

    /// <summary>Called on the UI thread whenever the feature toggle changes.</summary>
    public void UpdateSettings(AppSettings settings)
    {
        var wasEnabled = _enabled;
        _enabled = settings.ApplyObsAudio;

        if (_enabled && !wasEnabled && _watcher.RunningProcesses.Any(IsObs))
            _ = Task.Run(() => RepairAsync(_cts.Token));
    }

    /// <summary>
    /// The button. Creates the missing sources in OBS's current scene, re-points the existing ones,
    /// and puts each on the mix track plus its own. Always reports, whatever happened.
    /// </summary>
    public async Task SetupAsync()
    {
        try
        {
            await ApplyAsync(create: true, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (Describe(ex) is { } problem)
        {
            Activity?.Invoke($"OBS setup failed — {problem}");
        }
    }

    private void OnProcessesStarted(IReadOnlyCollection<string> names)
    {
        if (_enabled && names.Any(IsObs))
            _ = Task.Run(() => RepairAsync(_cts.Token));
    }

    private async Task RepairAsync(CancellationToken ct)
    {
        string? problem = null;

        try
        {
            foreach (var delay in RepairBackoff)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);

                if (!_enabled || !_watcher.RunningProcesses.Any(IsObs))
                    return;

                try
                {
                    await ApplyAsync(create: false, ct).ConfigureAwait(false);
                    _lastProblem = null;
                    return;
                }
                catch (Exception ex) when (IsNotReadyYet(ex))
                {
                    problem = Describe(ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (Describe(ex) is { } described)
        {
            problem = described;
        }

        if (problem is null || problem == _lastProblem)
            return;

        _lastProblem = problem;
        Activity?.Invoke($"OBS sources not checked — {problem}");
    }

    private async Task ApplyAsync(bool create, CancellationToken outer)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outer);
        timeout.CancelAfter(OperationTimeout);
        var ct = timeout.Token;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Unknown ids are not stale ids: with Sonar unreachable nothing is touched.
            var devices = await _client.GetAudioDevicesAsync(ct).ConfigureAwait(false)
                          ?? throw new SonarUnavailableException();

            var config = ObsServerConfig.Read();
            if (config is not { Enabled: true })
            {
                throw new ObsException(
                    "OBS's WebSocket server is off. Enable it in OBS under Tools → WebSocket Server Settings.");
            }

            await using var obs = await ObsWebSocketClient
                .ConnectAsync(config.Port, config.AuthRequired ? config.Password : "", ct)
                .ConfigureAwait(false);

            var inputs = (await obs.RequestAsync("GetInputList", null, ct).ConfigureAwait(false))
                .Expect()["inputs"] as JsonArray ?? new JsonArray();

            string? scene = null;
            if (create)
            {
                var current = (await obs.RequestAsync("GetCurrentProgramScene", null, ct).ConfigureAwait(false))
                    .Expect();
                scene = (string?)current["sceneName"] ?? (string?)current["currentProgramSceneName"]
                        ?? throw new ObsException("OBS did not report a current scene.");
            }

            List<string> created = [], repointed = [], unchanged = [], absent = [], foreign = [];

            foreach (var channel in Channels)
            {
                var deviceId = devices.FirstOrDefault(d =>
                    d.IsVad && !string.IsNullOrWhiteSpace(d.Id) &&
                    string.Equals(d.Role, channel.Role, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.DataFlow, channel.Flow, StringComparison.OrdinalIgnoreCase))?.Id;

                if (deviceId is null)
                {
                    absent.Add(channel.Source);
                    continue;
                }

                var input = inputs.OfType<JsonObject>().FirstOrDefault(i =>
                    string.Equals((string?)i["inputName"], channel.Source, StringComparison.OrdinalIgnoreCase));

                if (input is null)
                {
                    if (!create)
                        continue;

                    (await obs.RequestAsync("CreateInput", new JsonObject
                    {
                        ["sceneName"] = scene,
                        ["inputName"] = channel.Source,
                        ["inputKind"] = channel.Kind,
                        ["inputSettings"] = new JsonObject { ["device_id"] = deviceId },
                        ["sceneItemEnabled"] = true,
                    }, ct).ConfigureAwait(false)).Expect();

                    created.Add(channel.Source);
                }
                else
                {
                    // Somebody else's source that happens to share the name — a browser source called
                    // "Sonar Game", say. Not ours to re-point.
                    var kind = (string?)input["unversionedInputKind"] ?? (string?)input["inputKind"];
                    if (!string.Equals(kind, channel.Kind, StringComparison.OrdinalIgnoreCase))
                    {
                        foreign.Add(channel.Source);
                        continue;
                    }

                    var settings = (await obs.RequestAsync("GetInputSettings",
                        new JsonObject { ["inputName"] = channel.Source }, ct).ConfigureAwait(false)).Expect();

                    var currentId = settings["inputSettings"] is JsonObject s ? (string?)s["device_id"] : null;

                    if (string.Equals(currentId, deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        unchanged.Add(channel.Source);
                    }
                    else
                    {
                        // Overlay: only device_id changes, so use_device_timing and the rest survive.
                        (await obs.RequestAsync("SetInputSettings", new JsonObject
                        {
                            ["inputName"] = channel.Source,
                            ["inputSettings"] = new JsonObject { ["device_id"] = deviceId },
                            ["overlay"] = true,
                        }, ct).ConfigureAwait(false)).Expect();

                        repointed.Add(channel.Source);
                    }

                    if (create)
                        await EnsureInSceneAsync(obs, scene!, channel.Source, ct).ConfigureAwait(false);
                }

                if (create)
                {
                    var tracks = new JsonObject();
                    for (var track = 1; track <= TrackCount; track++)
                        tracks[track.ToString(CultureInfo.InvariantCulture)] = track == MixTrack || track == channel.Track;

                    (await obs.RequestAsync("SetInputAudioTracks", new JsonObject
                    {
                        ["inputName"] = channel.Source,
                        ["inputAudioTracks"] = tracks,
                    }, ct).ConfigureAwait(false)).Expect();
                }
            }

            if (create)
            {
                Activity?.Invoke(Summarise(scene!, created, repointed, unchanged, absent, foreign));

                foreach (var hint in await HintsAsync(obs, absent, foreign, ct).ConfigureAwait(false))
                    Activity?.Invoke(hint);
            }
            else if (repointed.Count > 0)
            {
                // The repair is silent unless it changed something: "all fine" at every OBS start is noise.
                Activity?.Invoke($"OBS: re-pointed {string.Join(", ", repointed)} at Sonar's current devices.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// An input is global to the scene collection but only heard in scenes that contain it, so an
    /// existing source is added to the current scene when it is not there yet. That is what makes the
    /// button the way to bring the sources into a second scene.
    /// </summary>
    private static async Task EnsureInSceneAsync(
        ObsWebSocketClient obs, string scene, string source, CancellationToken ct)
    {
        var item = await obs.RequestAsync("GetSceneItemId",
            new JsonObject { ["sceneName"] = scene, ["sourceName"] = source }, ct).ConfigureAwait(false);

        if (item.Ok)
            return;

        (await obs.RequestAsync("CreateSceneItem",
            new JsonObject { ["sceneName"] = scene, ["sourceName"] = source }, ct).ConfigureAwait(false)).Expect();
    }

    private static string Summarise(
        string scene, List<string> created, List<string> repointed, List<string> unchanged,
        List<string> absent, List<string> foreign)
    {
        List<string> parts = [];

        if (created.Count > 0)
            parts.Add($"created {string.Join(", ", created)}");
        if (repointed.Count > 0)
            parts.Add($"re-pointed {string.Join(", ", repointed)}");
        if (unchanged.Count > 0)
            parts.Add($"{string.Join(", ", unchanged)} already correct");
        if (absent.Count > 0)
            parts.Add($"skipped {string.Join(", ", absent)} (Sonar has no such device right now)");
        if (foreign.Count > 0)
            parts.Add($"left {string.Join(", ", foreign)} alone (a different kind of source has that name)");

        return $"OBS set up in scene \"{scene}\": {string.Join("; ", parts)}. " +
               $"Track {MixTrack} is the full mix; " +
               string.Join(", ", Channels.Select(c => $"{c.Track} {c.Source["Sonar ".Length..]}")) + ".";
    }

    /// <summary>
    /// Things the setup cannot fix because they are the user's OBS settings, said once per click.
    /// Best-effort: a failure here must not turn a finished setup into a reported failure.
    /// </summary>
    private static async Task<List<string>> HintsAsync(
        ObsWebSocketClient obs, List<string> absent, List<string> foreign, CancellationToken ct)
    {
        List<string> hints = [];

        try
        {
            var mode = await ProfileParameterAsync(obs, "Output", "Mode", ct).ConfigureAwait(false);

            if (!string.Equals(mode, "Advanced", StringComparison.OrdinalIgnoreCase))
            {
                hints.Add("OBS hint: Output mode is Simple, which records one audio track. Switch to Advanced " +
                          "(Settings → Output) and tick the audio tracks under Recording to keep the channels separate.");
            }
            else
            {
                var value = await ProfileParameterAsync(obs, "AdvOut", "RecTracks", ct).ConfigureAwait(false);
                var mask = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 1;

                var missing = Channels
                    .Where(c => !absent.Contains(c.Source) && !foreign.Contains(c.Source))
                    .Where(c => (mask & (1 << (c.Track - 1))) == 0)
                    .Select(c => c.Track.ToString(CultureInfo.InvariantCulture))
                    .ToList();

                if (missing.Count > 0)
                {
                    hints.Add($"OBS hint: audio track(s) {string.Join(", ", missing)} are not ticked under " +
                              "Settings → Output → Recording, so a recording will not carry them.");
                }
            }

            // OBS's own global devices are on every track by default. One that captures the Windows
            // default — usually Sonar Gaming — doubles that channel in the mix.
            var special = (await obs.RequestAsync("GetSpecialInputs", null, ct).ConfigureAwait(false)).Expect();
            List<string> live = [];

            foreach (var (_, node) in special)
            {
                if ((string?)node is not { Length: > 0 } name)
                    continue;

                var mute = await obs.RequestAsync("GetInputMute", new JsonObject { ["inputName"] = name }, ct)
                    .ConfigureAwait(false);

                if (mute.Ok && (bool?)mute.Data?["inputMuted"] == false)
                    live.Add(name);
            }

            if (live.Count > 0)
            {
                hints.Add($"OBS hint: the global audio device(s) {string.Join(", ", live)} are also live and " +
                          "may double a Sonar channel. Mute them, or disable them under Settings → Audio.");
            }
        }
        catch (Exception ex) when (ex is ObsException or WebSocketException or InvalidOperationException)
        {
            Debug.WriteLine($"OBS hints skipped: {ex.Message}");
        }

        return hints;
    }

    private static async Task<string?> ProfileParameterAsync(
        ObsWebSocketClient obs, string category, string name, CancellationToken ct)
    {
        var data = (await obs.RequestAsync("GetProfileParameter",
            new JsonObject { ["parameterCategory"] = category, ["parameterName"] = name }, ct)
            .ConfigureAwait(false)).Expect();

        return (string?)data["parameterValue"] ?? (string?)data["defaultParameterValue"];
    }

    private static bool IsObs(string process) =>
        ObsProcesses.Contains(process, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Failures that are expected in the seconds after OBS's process appears and worth another
    /// attempt: the socket not open yet, the scene collection not loaded, Sonar not up.
    /// </summary>
    private static bool IsNotReadyYet(Exception ex) =>
        ex is WebSocketException or SonarUnavailableException or ObsException { Code: ObsResponse.NotReady };

    /// <summary>A line for the activity log, or null for an exception that is not ours to swallow.</summary>
    private string? Describe(Exception ex) => ex switch
    {
        SonarUnavailableException => "Sonar is not running, so its devices are unknown.",
        ObsException => ex.Message,
        WebSocketException => "Could not reach OBS's WebSocket server. Is OBS running?",
        OperationCanceledException when !_cts.IsCancellationRequested => "OBS did not answer in time.",
        System.Text.Json.JsonException or InvalidOperationException or FormatException =>
            $"OBS sent something unexpected ({ex.Message}).",
        _ => null,
    };

    public void Dispose()
    {
        _watcher.Started -= OnProcessesStarted;
        _cts.Cancel();
        _cts.Dispose();
    }

    private sealed record Channel(string Source, string Role, string Flow, string Kind, int Track);

    private sealed class SonarUnavailableException : Exception;
}
