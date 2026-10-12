using System.Text.Json.Serialization;

namespace PresetSwitcherForSonar.Models;

/// <summary>
/// A Sonar preset this app has written a Volume Boost into, so it can be put back.
///
/// Persisted rather than kept in memory on purpose: the whole point of the restore option is that
/// unticking the feature tomorrow undoes what was applied today, and an in-memory list would be gone
/// by then. Without this record there is no way to tell a boost this app applied from one the user set
/// by hand — and putting the second kind back to zero would be destructive.
/// </summary>
public sealed class BoostedConfig
{
    [JsonPropertyName("configId")]
    public string ConfigId { get; set; } = "";

    /// <summary>The channel it was applied for, so releasing one channel does not disturb the other.</summary>
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";
}
