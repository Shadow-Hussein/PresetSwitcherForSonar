namespace SonarPresetSwitcher.Models;

/// <summary>
/// Sonar's mixer mode, as <c>GET /mode</c> returns it — a bare JSON string, not an object.
///
/// The two modes route audio through completely different tables. Classic sends each channel straight
/// to a Windows endpoint (<c>/classicRedirections</c>); streamer folds every channel into two mixes,
/// Monitoring and Streaming, each with its own endpoint (<c>/streamRedirections</c>).
///
/// Anything other than these two is treated as unknown rather than guessed at: the routing table for a
/// mode we do not recognise is by definition one we cannot read.
/// </summary>
public static class SonarMode
{
    public const string Classic = "classic";

    /// <summary>
    /// <c>"stream"</c>, not <c>"streamer"</c>. GG's own UI calls the mode "Streamer", and the wrong
    /// guess fails in two places at once and neither of them loudly: <c>PUT /mode/streamer</c> answers
    /// 400 <c>"Request validation error"</c>, and <c>GET /mode</c> returns <c>"stream"</c>, which then
    /// falls through every <see cref="Is"/> check into the unknown-mode branch. Confirmed against a
    /// live GG; <c>PUT /mode/stream</c> answers 200 and echoes <c>"stream"</c>.
    /// </summary>
    public const string Streamer = "stream";

    public static bool Is(string? mode, string known) =>
        string.Equals(mode, known, StringComparison.OrdinalIgnoreCase);

    public static bool IsKnown(string? mode) => Is(mode, Classic) || Is(mode, Streamer);

    /// <summary>
    /// "Classic" or "Streamer" for display. An unrecognised mode is returned as Sonar spelled it, so a
    /// future GG value shows up in the UI and the log instead of being silently normalised away.
    /// </summary>
    public static string Describe(string mode) => mode switch
    {
        _ when Is(mode, Classic) => "Classic",
        _ when Is(mode, Streamer) => "Streamer",
        _ => mode,
    };
}
