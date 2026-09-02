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

    /// <summary>
    /// Per-mapping "don't name a preset — use the default from Settings". Sonar's own ids are GUIDs,
    /// so this cannot collide with a real preset.
    ///
    /// A mapping that inherits is still a live mapping: it keeps its output device, recording device
    /// and streamer-mode flag, and still counts as the running winner. It just leaves the Game preset
    /// wherever the default puts it — which is how a game gets audio routing without a preset switch.
    /// </summary>
    public const string InheritId = "~global";

    /// <summary>
    /// The single shared instance the mapping picker holds. It must be a singleton, not a fresh
    /// object per rebuild: <see cref="SonarConfig"/> is a class, so the ComboBox matches its
    /// selection by reference.
    /// </summary>
    public static readonly SonarConfig Inherit = new() { Id = InheritId, Name = "Use default (Settings)" };

    public static bool IsInherit(string? id) =>
        string.Equals(id, InheritId, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Name;
}
