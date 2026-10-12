using System.Text.Json.Serialization;

namespace SonarPresetSwitcher.Models;

/// <summary>
/// One entry from Sonar's <c>GET /audioDevices</c>. Read for one reason only: <see cref="IsVad"/>
/// identifies Sonar's own virtual endpoints ("SteelSeries Sonar - Gaming" and friends), which must not
/// appear in the boost picker — boosting the channel that feeds them is circular.
///
/// This is the authoritative answer, but it needs Sonar running; <see cref="AudioDevice.LooksLikeSonarVirtual"/>
/// is the offline fallback.
/// </summary>
public sealed class SonarAudioDevice
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("friendlyName")]
    public string FriendlyName { get; set; } = "";

    [JsonPropertyName("dataFlow")]
    public string DataFlow { get; set; } = "";

    /// <summary>
    /// Which Sonar channel a virtual endpoint belongs to — <c>chatCapture</c> for Sonar's microphone,
    /// <c>none</c> for real hardware. Read by <see cref="Services.MicGainService"/> to find the one
    /// endpoint a microphone gain has to be written for.
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    /// <summary>True for a SteelSeries Sonar Virtual Audio Device.</summary>
    [JsonPropertyName("isVad")]
    public bool IsVad { get; set; }
}
