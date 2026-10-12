using System.Diagnostics;
using System.Text.Json.Nodes;
using PresetSwitcherForSonar.Models;

namespace PresetSwitcherForSonar.Services;

/// <summary>
/// Applies a saved Sonar Volume Boost to the Game and Media channels based on the device they are
/// playing out of.
///
/// Sonar has one Volume Boost per *preset*, not per device, so a level that is right on speakers is
/// wrong the moment the channel moves to a headset. This puts the level the user saved for the device
/// in use back.
///
/// **The boost lives inside the preset.** Sonar exposes no route that sets a gain directly — the whole
/// config has to be PUT back — so applying a boost edits whichever preset is selected on that channel
/// at the time. Two consequences fall out of that and neither is avoidable:
///
///   - The edit is permanent. A preset carries the boost until something changes it again.
///   - Every preset carries its own gain, so a preset switch (which is this app's whole other job)
///     silently replaces it. `_applied` is therefore keyed on the *pair* — device and config id — so a
///     switch re-arms the write while a device that has not moved does not.
///
/// **Both mixer modes are handled, through different routes.** Classic sends each channel straight to a
/// Windows endpoint; streamer folds them into a Monitoring and a Streaming mix. The rule keys on the
/// device the user *hears*, so streamer mode matches Monitoring and not Streaming — see
/// <see cref="ResolveDevicesAsync"/>. The write path is the same either way: configs are mode-independent.
///
/// Deliberately not part of <see cref="PresetCoordinator"/>: that is the preset decision layer, keyed
/// on running processes, and the only caller of <see cref="SonarClient.SelectAsync"/>. This is keyed on
/// Sonar's redirection state, so it polls on its own in the same shape as the coordinator's status loop.
/// </summary>
public sealed class ChannelBoostService : IDisposable
{
    /// <summary>Sonar's slider moves in 0.1 dB steps, so anything under half a step is already there.</summary>
    private const double GainEpsilon = 0.05;

    // Keys for the announcement latch, not values Sonar ever sends.
    private const string AnnouncedClassic = "classic";
    private const string AnnouncedStreamer = "streamer";
    private const string AnnouncedNoMonitoring = "streamer-no-monitoring";
    private const string AnnouncedUnknown = "unknown";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(10);

    private static readonly IReadOnlyDictionary<string, string> NoDevices =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The channels this manages. Both are spelled the same in <c>/classicRedirections</c>, in the roles
    /// of <c>/streamRedirections</c>, and in the configs' <c>virtualAudioDevice</c>, which is why no
    /// mapping table is needed; chat and mic are not, so adding them later would need one.
    /// </summary>
    private static readonly string[] Channels = [SonarRedirection.GameChannel, SonarRedirection.MediaChannel];

    private readonly SonarClient _client;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Channel to the device and config it was last applied for. Only touched by the loop.</summary>
    private readonly Dictionary<string, (string DeviceId, string ConfigId)> _applied =
        new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, string> _lastRaised = new(StringComparer.OrdinalIgnoreCase);
    private AppSettings _settings;
    private Task? _loop;
    private bool _reachable;

    /// <summary>Which routing condition was last written to the log. See <see cref="Announce"/>.</summary>
    private string? _announced;

    private volatile bool _invalidated;

    public ChannelBoostService(SonarClient client, AppSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    /// <summary>Human-readable lines for the activity log.</summary>
    public event Action<string>? Activity;

    /// <summary>
    /// Channel to the device id it is currently playing out of, for the "in use" markers in the UI.
    /// Raised only when the mapping actually changes, not on every poll.
    /// </summary>
    public event Action<IReadOnlyDictionary<string, string>>? RedirectionsChanged;

    /// <summary>Raised when <see cref="VirtualDeviceIds"/> has been learned or has changed.</summary>
    public event Action? VirtualDevicesChanged;

    /// <summary>
    /// Raised when <see cref="LastMode"/> changes, for the mode indicator in the status card. Carries
    /// null when Sonar goes away.
    /// </summary>
    public event Action<string?>? ModeChanged;

    /// <summary>
    /// Raised when this service has changed <see cref="AppSettings.BoostedConfigs"/> and it needs
    /// saving. Fired once at the end of a tick rather than per edit, so the list is never being
    /// mutated here while the UI thread is serialising it.
    /// </summary>
    public event Action? SettingsChanged;

    /// <summary>
    /// Sonar's own virtual endpoints, as Sonar itself reports them. Empty until Sonar has been reached
    /// once, which is why the picker also applies <see cref="AudioDevice.LooksLikeSonarVirtual"/>.
    /// </summary>
    public IReadOnlySet<string> VirtualDeviceIds { get; private set; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sonar's mixer mode as of the last poll, or null while Sonar is unreachable. This service already
    /// reads <c>/mode</c> every tick, so it publishes it rather than the app growing a second poller for
    /// one cheap route.
    /// </summary>
    public string? LastMode { get; private set; }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    /// <summary>Called when the user edits the rules in the UI.</summary>
    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;

        // Editing the rule for the device already in use has to take effect now, not at the next
        // redirection change. The loop clears its own state rather than this thread touching it.
        _invalidated = true;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Channel boost tick failed: {ex}");
            }

            try
            {
                await Task.Delay(_reachable ? PollInterval : ReconnectInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_invalidated)
        {
            _invalidated = false;
            _applied.Clear();

            // Re-raise the markers even if the redirections have not moved: a row just added for the
            // device already in use starts life without one, and would never be told otherwise.
            _lastRaised.Clear();
        }

        var settings = _settings;
        var enabled = settings.ApplyChannelBoost && settings.ChannelBoosts.Count > 0;

        // Read before the feature-disabled bail-out below, not after: the status card shows the mode
        // whether or not the boost is switched on, in the same way the ChatMix poll keeps running while
        // its feature is off so the UI can grey the control out.
        var mode = await _client.GetModeAsync(ct).ConfigureAwait(false);
        if (mode is null)
        {
            MarkUnreachable();
            return;
        }

        _reachable = true;
        RaiseMode(mode);

        // Once per connection, and needed before the first rule exists: it is what the device picker
        // filters Sonar's own endpoints out with.
        await RefreshVirtualDevicesAsync(ct).ConfigureAwait(false);

        // Nothing to resolve a device for: no rules to mark "in use", and nothing outstanding to put
        // back. Resolving anyway would poll a routing route every five seconds for a feature the user
        // has never touched — the mode above is enough for the status card on its own.
        if (settings.ChannelBoosts.Count == 0 && settings.BoostedConfigs.Count == 0)
            return;

        var devices = await ResolveDevicesAsync(mode, ct).ConfigureAwait(false);
        if (devices is null)
            return;

        // Raised before the bail-out below too, so the "in use" dots stay live while the user is still
        // setting rules up and has not ticked the feature on yet.
        RaiseRedirections(devices);

        if (!enabled)
        {
            _applied.Clear();

            // Still has to run when the feature is off — turning it off is itself a release trigger.
            // Only bail out entirely when there is also nothing outstanding to put back.
            if (settings.BoostedConfigs.Count == 0)
                return;
        }

        // Work out what is wanted before fetching any config: /configs/selected carries the full EQ
        // payload for every channel, so it is only worth pulling when a rule actually matches.
        var wanted = new List<(string Channel, string DeviceId, double Db, ChannelBoostRule Rule)>();

        if (enabled)
        {
            foreach (var channel in Channels)
            {
                if (!devices.TryGetValue(channel, out var deviceId) || string.IsNullOrWhiteSpace(deviceId))
                    continue;

                var rule = FindRule(settings, deviceId);
                var db = rule?.BoostFor(channel);

                if (db is null)
                {
                    // No rule, or this channel deliberately left blank. Drop any memory of it so moving
                    // back to a device that does have one applies again.
                    _applied.Remove(channel);
                    continue;
                }

                wanted.Add((channel, deviceId, db.Value, rule!));
            }
        }

        // Anything we boosted for a channel that is no longer covered gets put back first, so a device
        // change that swaps one rule for another does not briefly leave both applied.
        var active = wanted.Select(w => w.Channel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dirty = await ReleaseAsync(settings, active, ct).ConfigureAwait(false);

        if (wanted.Count == 0)
        {
            if (dirty)
                SettingsChanged?.Invoke();

            return;
        }

        var configs = await _client.GetSelectedConfigNodesAsync(ct).ConfigureAwait(false);
        if (configs is null)
        {
            MarkUnreachable();
            return;
        }

        foreach (var (channel, deviceId, db, rule) in wanted)
        {
            var config = SonarClient.FindChannelConfig(configs, channel);
            var configId = config?["id"]?.GetValue<string>();
            if (config is null || string.IsNullOrWhiteSpace(configId))
                continue;

            // On-change-only, over the pair: the device moving *or* the preset changing re-arms the
            // write, but neither happening leaves a boost the user adjusted by hand alone.
            if (_applied.TryGetValue(channel, out var last) &&
                string.Equals(last.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(last.ConfigId, configId, StringComparison.OrdinalIgnoreCase))
                continue;

            var current = SonarClient.ReadGeneralGain(config);
            if (current is not null && Math.Abs(current.Value - db) < GainEpsilon)
            {
                _applied[channel] = (deviceId, configId);
                continue;
            }

            if (!SonarClient.WriteGeneralGain(config, db))
                continue;

            if (await _client.PutConfigAsync(config, ct).ConfigureAwait(false))
            {
                _applied[channel] = (deviceId, configId);
                dirty |= Remember(settings, channel, configId);

                var preset = config["name"]?.GetValue<string>() ?? "the current preset";
                Activity?.Invoke(
                    $"{Label(channel)} volume boost set to {Format(db)} dB for {DeviceLabel(rule)} " +
                    $"(preset \"{preset}\").");
            }
            else
            {
                Activity?.Invoke(
                    $"Could not set the {Label(channel)} volume boost — Sonar refused the request.");
            }
        }

        if (dirty)
            SettingsChanged?.Invoke();
    }

    /// <summary>
    /// Which Windows endpoint the user is hearing each channel on, whichever way Sonar is routing.
    ///
    /// Classic reports that directly. Streamer mode does not: every channel goes into a Monitoring mix
    /// and a Streaming mix, each with its own endpoint. Only Monitoring is matched — it is the one the
    /// user hears, which is what a boost rule means, whereas Streaming normally feeds a virtual endpoint
    /// the rule picker filters out anyway. Matching both would also let two rules fight over the single
    /// gain a preset has.
    /// </summary>
    /// <returns>
    /// The map, empty when nothing is playing out of a device a rule could name; null when the tick must
    /// not go on — Sonar unreachable, or a mode whose routing table this does not know how to read.
    /// </returns>
    private async Task<IReadOnlyDictionary<string, string>?> ResolveDevicesAsync(
        string mode, CancellationToken ct)
    {
        if (SonarMode.Is(mode, SonarMode.Classic))
        {
            var redirections = await _client.GetClassicRedirectionsAsync(ct).ConfigureAwait(false);
            if (redirections is null)
            {
                MarkUnreachable();
                return null;
            }

            Announce(AnnouncedClassic, "Sonar is in classic mode.");
            return Map(channel => DeviceFor(redirections, channel));
        }

        if (SonarMode.Is(mode, SonarMode.Streamer))
        {
            var monitoringOn = await _client.GetStreamMonitoringEnabledAsync(ct).ConfigureAwait(false);
            if (monitoringOn is null)
            {
                MarkUnreachable();
                return null;
            }

            if (!monitoringOn.Value)
            {
                // Nothing is being played back to the user at all, so there is no device to key a rule
                // on, and anything outstanding is released rather than left applied.
                Announce(AnnouncedNoMonitoring,
                    "Stream monitoring is off — nothing for the volume boost to follow.");

                return NoDevices;
            }

            var redirections = await _client.GetStreamRedirectionsAsync(ct).ConfigureAwait(false);
            if (redirections is null)
            {
                MarkUnreachable();
                return null;
            }

            Announce(AnnouncedStreamer,
                "Sonar is in streamer mode — the volume boost now follows the Monitoring mix.");

            var monitoring = redirections.FirstOrDefault(r =>
                string.Equals(r.Id, StreamRedirection.MonitoringId, StringComparison.OrdinalIgnoreCase));

            // Blank until streamer mode has actually been set up with an output device.
            if (monitoring is null || string.IsNullOrWhiteSpace(monitoring.DeviceId))
                return NoDevices;

            return Map(channel => monitoring.IsRoleEnabled(channel) ? monitoring.DeviceId : null);
        }

        // A mode we do not recognise is a routing table we cannot read, so the device any rule would key
        // on is unknown. Hold everything — the release records included, which is why this returns null
        // rather than an empty map: dropping them would forget boosts we are still responsible for.
        Announce($"{AnnouncedUnknown}:{mode}",
            $"Per-device volume boost paused — Sonar reports an unknown mode (\"{mode}\").");

        _applied.Clear();
        RaiseRedirections(NoDevices);
        return null;
    }

    /// <summary>Builds the channel-to-device map from whichever lookup the current mode calls for.</summary>
    private static IReadOnlyDictionary<string, string> Map(Func<string, string?> deviceFor)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in Channels)
        {
            var deviceId = deviceFor(channel);
            if (!string.IsNullOrWhiteSpace(deviceId))
                map[channel] = deviceId;
        }

        return map;
    }

    /// <summary>
    /// One line per transition, never per tick. A single latch rather than a flag per condition, so no
    /// combination of mode and monitoring state can announce twice or go unannounced.
    ///
    /// Starting up in classic mode says nothing: that is the ordinary case, and the coordinator has just
    /// logged the connection.
    /// </summary>
    private void Announce(string key, string message)
    {
        if (string.Equals(_announced, key, StringComparison.Ordinal))
            return;

        var first = _announced is null;
        _announced = key;

        if (!first || !string.Equals(key, AnnouncedClassic, StringComparison.Ordinal))
            Activity?.Invoke(message);
    }

    /// <summary>
    /// Records a preset we have written a boost into, so it can be put back later. Only ever called
    /// after a PUT actually succeeded — a boost we merely found already at the target was set by the
    /// user, and claiming it would make the restore destructive.
    /// </summary>
    private static bool Remember(AppSettings settings, string channel, string configId)
    {
        if (settings.BoostedConfigs.Any(b =>
                string.Equals(b.ConfigId, configId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(b.Channel, channel, StringComparison.OrdinalIgnoreCase)))
            return false;

        settings.BoostedConfigs.Add(new BoostedConfig { ConfigId = configId, Channel = channel });
        return true;
    }

    /// <summary>
    /// Puts back every preset we boosted for a channel that is no longer covered by a rule — the
    /// feature switched off, the rule deleted or disabled, or the channel moved to a device without
    /// one.
    ///
    /// When the option is off the records are simply dropped: the boost stays where it is, which is the
    /// behaviour the feature has on its own.
    /// </summary>
    /// <returns>True when <see cref="AppSettings.BoostedConfigs"/> changed and needs saving.</returns>
    private async Task<bool> ReleaseAsync(
        AppSettings settings, IReadOnlySet<string> activeChannels, CancellationToken ct)
    {
        var stale = settings.BoostedConfigs
            .Where(b => !activeChannels.Contains(b.Channel))
            .ToList();

        if (stale.Count == 0)
            return false;

        if (!settings.RestoreBoostOnRelease)
        {
            foreach (var record in stale)
                settings.BoostedConfigs.Remove(record);

            return true;
        }

        // Not /configs/selected: a preset switch leaves our boost on the preset we switched away from,
        // and that one is no longer selected. Only fetched here, never on the polling path.
        var all = await _client.GetAllConfigNodesAsync(ct).ConfigureAwait(false);
        if (all is null)
        {
            // Keep the records and try again next tick, rather than forgetting what we changed.
            MarkUnreachable();
            return false;
        }

        var target = Math.Clamp(settings.RestoreBoostDb, ChannelBoostRule.MinDb, ChannelBoostRule.MaxDb);
        var changed = false;

        foreach (var record in stale)
        {
            var config = SonarClient.FindConfigById(all, record.ConfigId);

            // Preset deleted in GG, or already sitting at the target: nothing to write, but the record
            // has served its purpose either way.
            if (config is null || IsAt(config, target) || !SonarClient.WriteGeneralGain(config, target))
            {
                settings.BoostedConfigs.Remove(record);
                changed = true;
                continue;
            }

            if (!await _client.PutConfigAsync(config, ct).ConfigureAwait(false))
                continue;

            settings.BoostedConfigs.Remove(record);
            changed = true;

            var preset = config["name"]?.GetValue<string>() ?? "a preset";
            Activity?.Invoke(
                $"{Label(record.Channel)} volume boost returned to {Format(target)} dB " +
                $"(preset \"{preset}\").");
        }

        return changed;
    }

    private static bool IsAt(JsonObject config, double target)
    {
        var current = SonarClient.ReadGeneralGain(config);
        return current is not null && Math.Abs(current.Value - target) < GainEpsilon;
    }

    /// <summary>
    /// Learned once per connection rather than every tick: the endpoint list only changes when hardware
    /// is plugged in or GG rebuilds its virtual devices.
    /// </summary>
    private async Task RefreshVirtualDevicesAsync(CancellationToken ct)
    {
        if (VirtualDeviceIds.Count > 0)
            return;

        var devices = await _client.GetAudioDevicesAsync(ct).ConfigureAwait(false);
        if (devices is null)
            return;

        var ids = devices
            .Where(d => d.IsVad && !string.IsNullOrWhiteSpace(d.Id))
            .Select(d => d.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ids.Count == 0 || ids.SetEquals(VirtualDeviceIds))
            return;

        VirtualDeviceIds = ids;
        VirtualDevicesChanged?.Invoke();
    }

    /// <summary>
    /// Sonar is gone. Forget what was applied so it is re-applied on reconnect: GG can come back with
    /// different redirections, in a different mode, and with a different preset selected.
    /// </summary>
    private void MarkUnreachable()
    {
        _reachable = false;
        _applied.Clear();
        _announced = null;
        RaiseMode(null);
        RaiseRedirections(NoDevices);
    }

    private void RaiseMode(string? mode)
    {
        if (string.Equals(LastMode, mode, StringComparison.OrdinalIgnoreCase))
            return;

        LastMode = mode;
        ModeChanged?.Invoke(mode);
    }

    private void RaiseRedirections(IReadOnlyDictionary<string, string> map)
    {
        // The redirections are re-read every few seconds; only tell the UI when they actually moved.
        if (map.Count == _lastRaised.Count &&
            map.All(kv => _lastRaised.TryGetValue(kv.Key, out var seen) &&
                          string.Equals(seen, kv.Value, StringComparison.OrdinalIgnoreCase)))
            return;

        _lastRaised = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        RedirectionsChanged?.Invoke(map);
    }

    private static string? DeviceFor(IReadOnlyList<SonarRedirection> redirections, string channel) =>
        redirections
            .FirstOrDefault(r => string.Equals(r.Id, channel, StringComparison.OrdinalIgnoreCase))
            ?.DeviceId;

    private static ChannelBoostRule? FindRule(AppSettings settings, string deviceId) =>
        settings.ChannelBoosts.FirstOrDefault(r =>
            r.Enabled && string.Equals(r.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

    private static string Label(string channel) =>
        string.Equals(channel, SonarRedirection.GameChannel, StringComparison.OrdinalIgnoreCase)
            ? "Game"
            : "Media";

    private static string Format(double db) => db.ToString("0.#");

    private static string DeviceLabel(ChannelBoostRule rule)
    {
        var name = AudioDevice.CleanName(rule.DeviceName);
        return name.Length == 0 ? "the current output device" : name;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
