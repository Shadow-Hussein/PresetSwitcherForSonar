using System.Text.Json.Serialization;

namespace SonarPresetSwitcher.Models;

/// <summary>
/// Sonar's ChatMix — the balance between the game and chat audio streams, normally driven by a dial on
/// the headset. GG describes it as: the centre position is full volume of both streams, and turning
/// towards game reduces the chat volume and vice versa.
///
/// This is the shape of <c>GET /v1/chatMix</c>. Two traps live in that route:
///
///   - <b>The <c>/v1</c> prefix is real.</b> Every other route this app calls sits at the root
///     (<c>/configs</c>, <c>/mode</c>, <c>/classicRedirections</c>); <c>/chatMix</c> without the prefix
///     is a 404.
///   - <b>The balance binds from the query string, not the body.</b> <c>PUT /v1/chatMix?balance=0.4</c>
///     works; the same value sent as a JSON body answers 200 and writes 0, because the parameter simply
///     binds to its default. A silent wrong value, not an error.
///
/// The accepted range is -1 to 1 — Sonar answers 400 outside it. The sign convention is
/// <see cref="Min"/> = full game, <see cref="Max"/> = full chat; it is not derivable from the API,
/// which reports the balance and nothing else (the resulting gains are applied below
/// <c>/volumeSettings/classic</c>, whose numbers do not move with it).
///
/// Values do not round-trip exactly: Sonar stores the balance as a float, so 0.75 reads back as
/// 0.75000006. Compare with an epsilon, never for equality.
/// </summary>
public sealed class SonarChatMix
{
    public const double Min = -1;
    public const double Max = 1;
    public const double Center = 0;

    /// <summary>The one <see cref="State"/> value in which Sonar will accept a balance.</summary>
    public const string EnabledState = "enabled";

    [JsonPropertyName("balance")]
    public double Balance { get; set; }

    /// <summary>
    /// <c>"enabled"</c> when ChatMix is available. Sonar only offers it for sources with separate game
    /// and chat inputs, so a machine without such a headset reports something else — the full set of
    /// values is not documented, which is why anything other than <see cref="EnabledState"/> is treated
    /// as unavailable and logged verbatim.
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonIgnore]
    public bool IsUsable => string.Equals(State, EnabledState, StringComparison.OrdinalIgnoreCase);

    /// <summary>Clamps into range and onto the 0.01 step the UI slider offers.</summary>
    public static double Clamp(double balance) =>
        Math.Round(Math.Clamp(balance, Min, Max), 2, MidpointRounding.AwayFromZero);

    /// <summary>"Centred", "Game 40%" or "Chat 40%" — the balance as the UI spells it.</summary>
    public static string Describe(double balance)
    {
        var value = Clamp(balance);
        if (value == Center)
            return "Centred";

        var percent = (int)Math.Round(Math.Abs(value) * 100, MidpointRounding.AwayFromZero);
        return value < Center ? $"Game {percent}%" : $"Chat {percent}%";
    }
}
