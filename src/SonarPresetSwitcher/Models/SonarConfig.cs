using System.Text.Json.Serialization;

namespace SonarPresetSwitcher.Models;

/// <summary>
/// One entry from Sonar's <c>GET /configs</c>. The <c>data</c> property carries the actual EQ
/// payload and is channel-dependent; we never read or write it, so it is deliberately not modelled.
/// </summary>
public sealed class SonarConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>One of <c>game</c>, <c>chatRender</c>, <c>chatCapture</c>, <c>media</c>, <c>aux</c>.</summary>
    [JsonPropertyName("virtualAudioDevice")]
    public string VirtualAudioDevice { get; set; } = "";

    [JsonPropertyName("isFavorite")]
    public bool IsFavorite { get; set; }

    [JsonPropertyName("favoritePosition")]
    public int FavoritePosition { get; set; }

    /// <summary>True for SteelSeries-shipped presets, false for user-created ones.</summary>
    [JsonPropertyName("isPreset")]
    public bool IsPreset { get; set; }

    public const string GameDevice = "game";

    public override string ToString() => Name;
}
