using System.Text.Json.Serialization;

namespace PresetSwitcherForSonar.Models;

/// <summary>
/// A recording or streaming app — OBS, Discord, a voice recorder — and what Sonar should do while it
/// is running.
///
/// Deliberately not a <see cref="GameMapping"/>. A game row answers "which preset and which speakers";
/// this one answers "which microphone, and does Sonar need to be in streamer mode". The two lists are
/// independent, and an app may legitimately appear in both.
///
/// Keyed on <see cref="ProcessName"/> through <see cref="GameMapping.NormalizeProcessName"/>, the same
/// normalisation every other process comparison in the app uses.
/// </summary>
public sealed class RecordingApp
{
    /// <summary>Executable name without extension, lower-cased. This is the match key.</summary>
    [JsonPropertyName("processName")]
    public string ProcessName { get; set; } = "";

    /// <summary>Friendly name shown in the UI.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Windows *recording* device this app should capture from — the input half of what
    /// Settings → Sound → Volume mixer sets per app. This is what points the app at one of Sonar's
    /// virtual microphones.
    ///
    /// <see cref="AudioDevice.UnmanagedId"/> means "leave the input alone"; empty clears the override,
    /// which is a deliberate action rather than the absence of one. There is no inherit value: unlike
    /// the output device, there is no global default input to inherit from.
    ///
    /// New rows are given Sonar's stream channel when one is added, not here — the enumeration is not
    /// available to a model initializer, and a default resolved at load time would overwrite a choice.
    /// </summary>
    [JsonPropertyName("inputDeviceId")]
    public string InputDeviceId { get; set; } = AudioDevice.UnmanagedId;

    /// <summary>Cached so the picker still shows a sensible label if the device is unplugged.</summary>
    [JsonPropertyName("inputDeviceName")]
    public string InputDeviceName { get; set; } = "";

    /// <summary>
    /// Puts Sonar into streamer mode while this app is running, and back to the mode it was in once it
    /// exits. Off by default — it changes Sonar's whole mixer, not just this app.
    /// </summary>
    [JsonPropertyName("requireStreamerMode")]
    public bool RequireStreamerMode { get; set; }
}
