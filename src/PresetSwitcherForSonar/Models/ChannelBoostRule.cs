using System.Text.Json.Serialization;

namespace PresetSwitcherForSonar.Models;

/// <summary>
/// A saved Sonar Volume Boost, in dB, per channel for one physical output device.
///
/// The boost is Sonar's <c>generalGain</c> — the "Volume boost" slider that sits above the equalizer,
/// 0 to 12 dB in steps of 0.1. It is applied *before* processing, which is why it is the right lever
/// for a device that is simply quieter than the others.
///
/// <see cref="DeviceId"/> is the raw MMDevice id, which is exactly what Sonar reports in
/// <c>/classicRedirections</c>, so the two join with no translation.
///
/// A null level means "leave that channel alone", so a rule can boost Game without touching Media.
/// </summary>
public sealed class ChannelBoostRule
{
    public const double MinDb = 0;
    public const double MaxDb = 12;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    /// <summary>Cached so the picker still shows a sensible label if the device is unplugged.</summary>
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>0-12 dB, or null to leave the Game channel at whatever its preset carries.</summary>
    [JsonPropertyName("gameBoostDb")]
    public double? GameBoostDb { get; set; }

    /// <summary>0-12 dB, or null to leave the Media channel at whatever its preset carries.</summary>
    [JsonPropertyName("mediaBoostDb")]
    public double? MediaBoostDb { get; set; }

    /// <summary>Clamps into range and onto Sonar's 0.1 dB step.</summary>
    public static double? Clamp(double? db) =>
        db is null ? null : Math.Round(Math.Clamp(db.Value, MinDb, MaxDb), 1, MidpointRounding.AwayFromZero);

    /// <summary>The boost saved for one of <see cref="SonarRedirection"/>'s channel ids.</summary>
    public double? BoostFor(string channel) => channel switch
    {
        SonarRedirection.GameChannel => Clamp(GameBoostDb),
        SonarRedirection.MediaChannel => Clamp(MediaBoostDb),
        _ => null,
    };
}
