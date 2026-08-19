using System.Diagnostics;
using System.Text.Json.Nodes;
using SonarPresetSwitcher.Models;

namespace SonarPresetSwitcher.Services;

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
/// Deliberately not part of <see cref="PresetCoordinator"/>: that is the preset decision layer, keyed
/// on running processes, and the only caller of <see cref="SonarClient.SelectAsync"/>. This is keyed on
/// Sonar's redirection state, so it polls on its own in the same shape as the coordinator's status loop.
/// </summary>
public sealed class ChannelBoostService : IDisposable
{
    private const string ClassicMode = "classic";

    /// <summary>Sonar's slider moves in 0.1 dB steps, so anything under half a step is already there.</summary>
    private const double GainEpsilon = 0.05;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The channels this manages. Both are spelled the same in <c>/classicRedirections</c> and in the
    /// configs' <c>virtualAudioDevice</c>, which is why no mapping table is needed; chat and mic are
    /// not, so adding them later would need one.
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
    private bool _streamerModeAnnounced;
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
        }

        var settings = _settings;
        var enabled = settings.ApplyChannelBoost && settings.ChannelBoosts.Count > 0;

        if (!enabled)
        {
            _applied.Clear();

            // Still has to run when the feature is off — turning it off is itself a release trigger.
            // Only bail out entirely when there is also nothing outstanding to put back.
            if (settings.BoostedConfigs.Count == 0)
                return;
        }

        var mode = await _client.GetModeAsync(ct).ConfigureAwait(false);
        if (mode is null)
        {
            MarkUnreachable();
            return;
        }

        _reachable = true;
        await RefreshVirtualDevicesAsync(ct).ConfigureAwait(false);

        if (!string.Equals(mode, ClassicMode, StringComparison.OrdinalIgnoreCase))
        {
            // Streamer mode redirects through a different set of routes entirely, so the device this
            // reads would not be the one in use. Say so once, not every five seconds.
            if (!_streamerModeAnnounced)
            {
                _streamerModeAnnounced = true;
                Activity?.Invoke("Per-device volume boost paused — Sonar is in streamer mode.");
            }

            _applied.Clear();
            return;
        }

        if (_streamerModeAnnounced)
        {
            _streamerModeAnnounced = false;
            Activity?.Invoke("Sonar is back in classic mode — per-device volume boost resumed.");
        }

        var redirections = await _client.GetClassicRedirectionsAsync(ct).ConfigureAwait(false);
        if (redirections is null)
        {
            MarkUnreachable();
            return;
        }

        RaiseRedirections(redirections);

        // Work out what is wanted before fetching any config: /configs/selected carries the full EQ
        // payload for every channel, so it is only worth pulling when a rule actually matches.
        var wanted = new List<(string Channel, string DeviceId, double Db, ChannelBoostRule Rule)>();

        if (enabled)
        {
            foreach (var channel in Channels)
            {
                var deviceId = DeviceFor(redirections, channel);
                if (string.IsNullOrWhiteSpace(deviceId))
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
    /// different redirections, and a different preset selected.
    /// </summary>
    private void MarkUnreachable()
    {
        _reachable = false;
        _applied.Clear();
        RaiseRedirections([]);
    }

    private void RaiseRedirections(IReadOnlyList<SonarRedirection> redirections)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in Channels)
        {
            var deviceId = DeviceFor(redirections, channel);
            if (!string.IsNullOrWhiteSpace(deviceId))
                map[channel] = deviceId;
        }

        // The redirections are re-read every few seconds; only tell the UI when they actually moved.
        if (map.Count == _lastRaised.Count &&
            map.All(kv => _lastRaised.TryGetValue(kv.Key, out var seen) &&
                          string.Equals(seen, kv.Value, StringComparison.OrdinalIgnoreCase)))
            return;

        _lastRaised = map;
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
