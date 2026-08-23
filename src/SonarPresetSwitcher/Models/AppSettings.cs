using System.Text.Json.Serialization;

namespace SonarPresetSwitcher.Models;

public sealed class AppSettings
{
    /// <summary>Preset restored when no mapped game is running. Empty means "leave as-is".</summary>
    [JsonPropertyName("defaultPresetId")]
    public string DefaultPresetId { get; set; } = "";

    [JsonPropertyName("defaultPresetName")]
    public string DefaultPresetName { get; set; } = "";

    /// <summary>
    /// Output device used by mappings that are set to "Use default". Empty means the Windows system
    /// default, i.e. no per-app override at all.
    /// </summary>
    [JsonPropertyName("defaultOutputDeviceId")]
    public string DefaultOutputDeviceId { get; set; } = AudioDevice.SystemDefaultId;

    /// <summary>Cached so the picker still shows a sensible label if the device is unplugged.</summary>
    [JsonPropertyName("defaultOutputDeviceName")]
    public string DefaultOutputDeviceName { get; set; } = "";

    /// <summary>
    /// Puts the Windows *system* default playback device back to
    /// <see cref="KeepWindowsDefaultDeviceId"/> whenever another app moves it — SteelSeries GG does
    /// exactly that at every startup. Off by default: this is the one setting that changes Windows
    /// itself rather than a per-app override.
    /// </summary>
    [JsonPropertyName("keepWindowsDefault")]
    public bool KeepWindowsDefault { get; set; }

    [JsonPropertyName("keepWindowsDefaultDeviceId")]
    public string KeepWindowsDefaultDeviceId { get; set; } = "";

    /// <summary>Cached so the picker still shows a sensible label if the device is unplugged.</summary>
    [JsonPropertyName("keepWindowsDefaultDeviceName")]
    public string KeepWindowsDefaultDeviceName { get; set; } = "";

    [JsonPropertyName("showNotifications")]
    public bool ShowNotifications { get; set; } = true;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    [JsonPropertyName("minimizeToTray")]
    public bool MinimizeToTray { get; set; } = true;

    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 2;

    [JsonPropertyName("mappings")]
    public List<GameMapping> Mappings { get; set; } = [];

    /// <summary>
    /// Applies a saved Volume Boost to Sonar's Game and Media channels whenever Sonar starts playing
    /// that channel out of a device listed in <see cref="ChannelBoosts"/>. Off by default: it writes to
    /// the Sonar preset that is selected at the time.
    /// </summary>
    [JsonPropertyName("applyChannelBoost")]
    public bool ApplyChannelBoost { get; set; }

    [JsonPropertyName("channelBoosts")]
    public List<ChannelBoostRule> ChannelBoosts { get; set; } = [];

    /// <summary>
    /// Puts the boost back to <see cref="RestoreBoostDb"/> when a preset stops being covered by a rule
    /// — the feature turned off, the rule deleted or disabled, or the channel moved to a device with no
    /// rule. Off by default, because on its own the feature leaves what it applied in place.
    /// </summary>
    [JsonPropertyName("restoreBoostOnRelease")]
    public bool RestoreBoostOnRelease { get; set; }

    /// <summary>What a released preset goes back to. 0 is Sonar's own default.</summary>
    [JsonPropertyName("restoreBoostDb")]
    public double RestoreBoostDb { get; set; }

    /// <summary>
    /// The presets this app has written a boost into. Persisted so a restore still works after a
    /// restart, and so a boost the *user* set by hand is never mistaken for one of ours.
    /// </summary>
    [JsonPropertyName("boostedConfigs")]
    public List<BoostedConfig> BoostedConfigs { get; set; } = [];

    /// <summary>
    /// Pushes <see cref="ChatMixBalance"/> to Sonar once each time this app connects to it — at
    /// startup, and again after a GG restart. Deliberately not held: the headset dial stays usable
    /// afterwards, and the value is only put back at the next reconnect. Off by default.
    /// </summary>
    [JsonPropertyName("applyChatMix")]
    public bool ApplyChatMix { get; set; }

    /// <summary>
    /// The game/chat balance to start each session at: -1 is full game, 1 is full chat, 0 is Sonar's
    /// own centre. See <see cref="SonarChatMix"/> for the range Sonar accepts.
    /// </summary>
    [JsonPropertyName("chatMixBalance")]
    public double ChatMixBalance { get; set; }

    /// <summary>Derived, not persisted — it was being written to settings.json as a stray key.</summary>
    [JsonIgnore]
    public int ClampedPollInterval => Math.Clamp(PollIntervalSeconds, 1, 30);
}
