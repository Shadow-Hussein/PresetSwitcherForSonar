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

    /// <summary>Derived, not persisted — it was being written to settings.json as a stray key.</summary>
    [JsonIgnore]
    public int ClampedPollInterval => Math.Clamp(PollIntervalSeconds, 1, 30);
}
