using System.Text.Json.Serialization;

namespace SonarPresetSwitcher.Models;

/// <summary>
/// One entry from Sonar's <c>GET /streamRedirections</c> — the streamer-mode counterpart of
/// <see cref="SonarRedirection"/>.
///
/// Streamer mode folds every channel into two mixes: <c>monitoring</c> (what you hear) and
/// <c>streaming</c> (what goes out), plus a <c>mic</c> entry. Each carries the Windows endpoint that
/// mix plays out of, so <c>monitoring</c>'s <see cref="DeviceId"/> is the direct analogue of a classic
/// redirection's — same raw MMDevice id, directly comparable with <see cref="AudioDevice.Id"/>.
///
/// The roles in <see cref="Status"/> are spelled <c>chatRender</c>/<c>chatCapture</c> where
/// <c>/classicRedirections</c> says <c>chat</c>/<c>mic</c>. Game and Media are spelled the same in both,
/// which is why they need no mapping table.
///
/// This route answers 200 in classic mode too, with blank device ids — it is not a mode probe.
/// </summary>
public sealed class StreamRedirection
{
    /// <summary>What you hear. The only mix the volume boost matches against.</summary>
    public const string MonitoringId = "monitoring";

    /// <summary>
    /// What goes out to the stream. Deliberately not matched: it normally feeds a virtual endpoint that
    /// the boost picker filters out anyway, and letting both mixes match would let two rules fight over
    /// the one gain a preset has.
    /// </summary>
    public const string StreamingId = "streaming";

    [JsonPropertyName("streamRedirectionId")]
    public string Id { get; set; } = "";

    /// <summary>The raw MMDevice id, in the same form as <see cref="AudioDevice.Id"/>.</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    /// <summary>Which channels this mix carries. Empty on the <c>mic</c> entry.</summary>
    [JsonPropertyName("status")]
    public List<StreamRedirectionRole> Status { get; set; } = [];

    [JsonPropertyName("isRunning")]
    public bool IsRunning { get; set; }

    /// <summary>
    /// True when this mix carries the given channel. A role missing from <see cref="Status"/> counts as
    /// off — an empty list is a shape Sonar really returns, not a parse failure.
    /// </summary>
    public bool IsRoleEnabled(string role) =>
        Status.Any(s => s.IsEnabled && string.Equals(s.Role, role, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One channel's on/off state within a <see cref="StreamRedirection"/>.</summary>
public sealed class StreamRedirectionRole
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; }
}
