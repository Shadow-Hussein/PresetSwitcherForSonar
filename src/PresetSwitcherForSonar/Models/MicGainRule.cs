using System.Text.Json.Serialization;

namespace PresetSwitcherForSonar.Models;

/// <summary>
/// A saved digital gain, in dB, for Sonar's microphone while Sonar is reading from one particular
/// physical microphone.
///
/// This is not a Sonar setting. Sonar's mic config carries no gain at all — only the Volume Stabilizer
/// (a compressor), the noise stages and the EQ — and its mic volume is validated to 0-1, so there is
/// nothing above 100% to ask for. The gain is Equalizer APO's <c>Preamp</c> instead, which is why it
/// can go past what a level slider allows. See <see cref="Services.MicGainService"/>.
///
/// <see cref="DeviceId"/> is the raw MMDevice id of the <b>physical</b> capture endpoint, which is
/// exactly what Sonar reports as <c>mic</c> in <c>/classicRedirections</c>, so the two join with no
/// translation. It is the key the rule is chosen by, not where the gain is written: that is always
/// Sonar's own virtual microphone.
/// </summary>
public sealed class MicGainRule
{
    public const double MinDb = 0;

    /// <summary>
    /// Equalizer APO itself has no ceiling. This one is ours: +30 dB is already a factor of ~32, and
    /// past it the result is clipping rather than loudness.
    /// </summary>
    public const double MaxDb = 30;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    /// <summary>Cached so the picker still shows a sensible label if the device is unplugged.</summary>
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("gainDb")]
    public double GainDb { get; set; }

    /// <summary>Clamps into range and onto a 0.5 dB step.</summary>
    public static double Clamp(double db) =>
        Math.Round(Math.Clamp(db, MinDb, MaxDb) * 2, MidpointRounding.AwayFromZero) / 2;
}
