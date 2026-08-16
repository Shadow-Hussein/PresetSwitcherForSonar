using System.Text.Json.Serialization;

namespace SonarPresetSwitcher.Models;

/// <summary>Binds a process to a Sonar Game-channel preset.</summary>
public sealed class GameMapping
{
    /// <summary>Executable name without extension, lower-cased. This is the match key.</summary>
    [JsonPropertyName("processName")]
    public string ProcessName { get; set; } = "";

    /// <summary>Friendly name shown in the UI.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("presetId")]
    public string PresetId { get; set; } = "";

    /// <summary>Cached so rows still render sensibly while Sonar is unreachable.</summary>
    [JsonPropertyName("presetName")]
    public string PresetName { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Windows output device this game should play through. <see cref="AudioDevice.InheritId"/> means
    /// "use the global default from Settings", empty means the system default (no override).
    /// Settings files written before this feature existed have no such key, so they land on the
    /// initializer and inherit — which is the sensible upgrade path.
    /// </summary>
    [JsonPropertyName("outputDeviceId")]
    public string OutputDeviceId { get; set; } = AudioDevice.InheritId;

    /// <summary>Cached so the picker still shows a sensible label if the device is unplugged.</summary>
    [JsonPropertyName("outputDeviceName")]
    public string OutputDeviceName { get; set; } = "";

    public static string NormalizeProcessName(string value)
    {
        var name = value.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.ToLowerInvariant();
    }
}
