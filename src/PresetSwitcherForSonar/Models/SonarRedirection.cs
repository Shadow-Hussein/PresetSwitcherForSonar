using System.Text.Json.Serialization;

namespace PresetSwitcherForSonar.Models;

/// <summary>
/// One entry from Sonar's <c>GET /classicRedirections</c> — which Windows endpoint a channel is
/// currently playing out of.
///
/// The channel ids here are <c>game</c>, <c>media</c>, <c>chat</c>, <c>aux</c> and <c>mic</c>, which
/// only partly match the ids <c>/volumeSettings/classic</c> uses (<c>chatRender</c>/<c>chatCapture</c>
/// instead of <c>chat</c>/<c>mic</c>). Game and Media are spelled the same in both, which is why they
/// need no mapping table here.
/// </summary>
public sealed class SonarRedirection
{
    public const string GameChannel = "game";
    public const string MediaChannel = "media";

    /// <summary>The capture side: the physical microphone Sonar is reading from.</summary>
    public const string MicChannel = "mic";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>The raw MMDevice id, in the same form as <see cref="AudioDevice.Id"/>.</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("isRunning")]
    public bool IsRunning { get; set; }
}
