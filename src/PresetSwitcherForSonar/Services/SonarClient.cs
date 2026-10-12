using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PresetSwitcherForSonar.Models;

namespace PresetSwitcherForSonar.Services;

/// <summary>
/// Thin wrapper over Sonar's undocumented local REST API.
///
///   GET  /configs                 all presets, each carrying isFavorite / favoritePosition
///                                 (see <see cref="GetGamePresetsAsync"/>)
///   GET  /configs/selected        the active preset per channel
///   PUT  /configs/{id}/select     switch the channel that preset belongs to
///   GET  /mode                    cheap liveness probe, and classic vs. streamer mixer mode
///   PUT  /mode/{mode}             switch the mixer mode — value in the path, no body
///   GET  /classicRedirections     which Windows endpoint each channel plays out of, in classic mode
///   GET  /streamRedirections      the same for streamer mode: the Monitoring and Streaming mixes
///   GET  /streamRedirections/isStreamMonitoringEnabled
///                                 whether the Monitoring mix is on at all
///   GET  /audioDevices            every endpoint Sonar knows, with isVad
///   PUT  /configs                 save a whole config — the only way to set data.generalGain
///   GET  /v1/chatMix              the game/chat balance, and whether ChatMix is available at all
///   PUT  /v1/chatMix?balance=     set the balance — note the /v1 prefix and the query parameter
///
/// Every call goes through <see cref="SendAsync"/>, which re-runs discovery and retries once when the
/// transport fails — that is what makes the app survive a GG restart handing Sonar a new port.
/// </summary>
public sealed class SonarClient : IDisposable
{
    private readonly SonarDiscovery _discovery;
    private readonly HttpClient _http;

    public SonarClient(SonarDiscovery discovery)
    {
        _discovery = discovery;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>Host:port of the connection last used successfully, for display purposes.</summary>
    public string? Endpoint
    {
        get
        {
            var url = _discovery.CachedBaseUrl;
            return url is null ? null : new Uri(url).Authority;
        }
    }

    public async Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/mode", ct).ConfigureAwait(false);
        return response is { IsSuccessStatusCode: true };
    }

    /// <summary>
    /// Every Game-channel preset, starred ones first in mixer order and the rest alphabetically.
    ///
    /// Favourites are surfaced first rather than filtered to, so un-starring a preset in GG no longer
    /// silently breaks a mapping that points at it.
    /// </summary>
    public async Task<IReadOnlyList<SonarConfig>> GetGamePresetsAsync(CancellationToken ct = default)
    {
        var all = await GetJsonAsync<List<SonarConfig>>("/configs", ct).ConfigureAwait(false);
        if (all is null)
            return [];

        return all
            .Where(c => string.Equals(c.VirtualAudioDevice, SonarConfig.GameDevice, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.IsFavorite)
            .ThenBy(c => c.IsFavorite ? c.FavoritePosition : int.MaxValue)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The preset currently active on the Game channel, or null when unreachable.</summary>
    public async Task<SonarConfig?> GetSelectedGameConfigAsync(CancellationToken ct = default)
    {
        var selected = await GetJsonAsync<List<SonarConfig>>("/configs/selected", ct).ConfigureAwait(false);
        return selected?.FirstOrDefault(c =>
            string.Equals(c.VirtualAudioDevice, SonarConfig.GameDevice, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Applies a preset. Sonar infers the channel from the preset itself.</summary>
    public async Task<bool> SelectAsync(string configId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(configId))
            return false;

        using var response = await SendAsync(HttpMethod.Put, $"/configs/{configId}/select", ct).ConfigureAwait(false);
        return response is { IsSuccessStatusCode: true };
    }

    /// <summary>
    /// Sonar's mixer mode — <c>"classic"</c> or <c>"stream"</c> (GG's UI calls the latter
    /// "Streamer"; the wire value is not). Returned as a bare JSON string, not an object.
    /// </summary>
    public async Task<string?> GetModeAsync(CancellationToken ct = default) =>
        await GetJsonAsync<string>("/mode", ct).ConfigureAwait(false);

    /// <summary>
    /// Which Windows endpoint each channel is currently playing out of. The <c>deviceId</c> values are
    /// raw MMDevice ids, directly comparable with <see cref="AudioDevice.Id"/>.
    ///
    /// Null means Sonar could not be reached, which an empty list would hide.
    /// </summary>
    public async Task<IReadOnlyList<SonarRedirection>?> GetClassicRedirectionsAsync(
        CancellationToken ct = default) =>
        await GetJsonAsync<List<SonarRedirection>>("/classicRedirections", ct).ConfigureAwait(false);

    /// <summary>
    /// Switches Sonar's mixer mode. The value goes in the path, not a body — <c>PUT /mode/stream</c>
    /// (a GET on the same path answers 405 with <c>Allow: PUT</c>, which is how the verb was settled).
    ///
    /// The value is the wire spelling, not GG's label: <c>PUT /mode/streamer</c> answers 400
    /// <c>"Request validation error"</c>. Pass <see cref="SonarMode.Streamer"/> rather than a literal.
    ///
    /// This changes Sonar's whole mixer, so <see cref="PresetCoordinator"/> is the only caller, and only
    /// when a mapping asks for it.
    /// </summary>
    public async Task<bool> SetModeAsync(string mode, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Put, $"/mode/{Uri.EscapeDataString(mode)}", ct)
            .ConfigureAwait(false);

        return response is { IsSuccessStatusCode: true };
    }

    /// <summary>
    /// Streamer mode's routing table: the Monitoring and Streaming mixes, each with the endpoint it
    /// plays out of and which channels it carries.
    ///
    /// Answers 200 in classic mode too, with blank device ids, so it is never a way to tell the mode —
    /// use <see cref="GetModeAsync"/> for that.
    ///
    /// Null means Sonar could not be reached, which an empty list would hide.
    /// </summary>
    public async Task<IReadOnlyList<StreamRedirection>?> GetStreamRedirectionsAsync(
        CancellationToken ct = default) =>
        await GetJsonAsync<List<StreamRedirection>>("/streamRedirections", ct).ConfigureAwait(false);

    /// <summary>
    /// Whether the Monitoring mix is on. With it off there is no endpoint playing streamer-mode audio
    /// back to the user at all.
    ///
    /// Deserialised as <c>bool?</c> so an unreachable Sonar stays distinguishable from a genuine false.
    /// </summary>
    public async Task<bool?> GetStreamMonitoringEnabledAsync(CancellationToken ct = default) =>
        await GetJsonAsync<bool?>("/streamRedirections/isStreamMonitoringEnabled", ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Every audio endpoint Sonar knows about. Read only for <c>isVad</c>, which is the authoritative
    /// way to tell Sonar's own virtual endpoints from real hardware.
    /// </summary>
    public async Task<IReadOnlyList<SonarAudioDevice>?> GetAudioDevicesAsync(CancellationToken ct = default) =>
        await GetJsonAsync<List<SonarAudioDevice>>("/audioDevices", ct).ConfigureAwait(false);

    /// <summary>
    /// The selected config for every channel, as the raw JSON Sonar returned.
    ///
    /// Raw rather than <see cref="SonarConfig"/> on purpose: the only way to change a single field is
    /// to PUT the whole config back, and anything this app failed to model would be dropped on the way
    /// through. The <c>data</c> payload alone carries a ten-band parametric EQ, virtual surround
    /// positions and more, so round-tripping the untouched node is the only safe edit.
    /// </summary>
    public async Task<JsonArray?> GetSelectedConfigNodesAsync(CancellationToken ct = default) =>
        await GetJsonAsync<JsonArray>("/configs/selected", ct).ConfigureAwait(false);

    /// <summary>
    /// Every config, raw. Only needed when putting a boost back: a preset switch leaves our boost on
    /// the preset we switched away from, which <c>/configs/selected</c> no longer lists. Much larger
    /// than the selected list, so this is fetched on release and never on the polling path.
    /// </summary>
    public async Task<JsonArray?> GetAllConfigNodesAsync(CancellationToken ct = default) =>
        await GetJsonAsync<JsonArray>("/configs", ct).ConfigureAwait(false);

    /// <summary>Saves a config previously read by <see cref="GetSelectedConfigNodesAsync"/>.</summary>
    public async Task<bool> PutConfigAsync(JsonObject config, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Put, "/configs", ct, config.ToJsonString())
            .ConfigureAwait(false);

        return response is { IsSuccessStatusCode: true };
    }

    /// <summary>
    /// The current ChatMix balance and availability, or null when Sonar could not be reached.
    ///
    /// The <c>/v1</c> prefix is not a typo — see <see cref="SonarChatMix"/>; every other route here
    /// sits at the root, and <c>/chatMix</c> answers 404.
    /// </summary>
    public async Task<SonarChatMix?> GetChatMixAsync(CancellationToken ct = default) =>
        await GetJsonAsync<SonarChatMix>("/v1/chatMix", ct).ConfigureAwait(false);

    /// <summary>
    /// Sets the ChatMix balance, -1 (game) to 1 (chat). Sonar answers 400 outside that range.
    ///
    /// The value goes in the query string. Sent as a JSON body instead, Sonar answers 200 and writes 0
    /// — the parameter binds to its default — so this must never be "tidied" into a body like
    /// <see cref="PutConfigAsync"/>. Invariant formatting for the same reason: a comma decimal
    /// separator from the user's locale would not parse.
    /// </summary>
    public async Task<bool> SetChatMixAsync(double balance, CancellationToken ct = default)
    {
        var value = SonarChatMix.Clamp(balance).ToString(CultureInfo.InvariantCulture);

        using var response = await SendAsync(HttpMethod.Put, $"/v1/chatMix?balance={value}", ct)
            .ConfigureAwait(false);

        return response is { IsSuccessStatusCode: true };
    }

    /// <summary>Picks one config out of <see cref="GetAllConfigNodesAsync"/> by id.</summary>
    public static JsonObject? FindConfigById(JsonArray configs, string id)
    {
        foreach (var node in configs)
        {
            if (node is JsonObject config &&
                string.Equals(config["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
                return config;
        }

        return null;
    }

    /// <summary>Picks one channel's config out of <see cref="GetSelectedConfigNodesAsync"/>.</summary>
    public static JsonObject? FindChannelConfig(JsonArray configs, string channel)
    {
        foreach (var node in configs)
        {
            if (node is JsonObject config &&
                string.Equals(
                    config["virtualAudioDevice"]?.GetValue<string>(),
                    channel,
                    StringComparison.OrdinalIgnoreCase))
                return config;
        }

        return null;
    }

    /// <summary>
    /// Sonar's "Volume boost" for a config — <c>data.generalGain</c>, 0 to 12 dB in 0.1 steps, applied
    /// before processing. There is no route that sets it directly; the whole config has to go back.
    /// </summary>
    public static double? ReadGeneralGain(JsonObject config) =>
        config["data"] is JsonObject data && data["generalGain"] is JsonValue gain &&
        gain.TryGetValue<double>(out var value)
            ? value
            : null;

    /// <summary>Writes <c>data.generalGain</c> in place, leaving every other field untouched.</summary>
    public static bool WriteGeneralGain(JsonObject config, double db)
    {
        if (config["data"] is not JsonObject data)
            return false;

        data["generalGain"] = JsonValue.Create(db);
        return true;
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, ct).ConfigureAwait(false);
        if (response is null || !response.IsSuccessStatusCode)
            return default;

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or TaskCanceledException)
        {
            return default;
        }
    }

    /// <summary>
    /// Issues the request, and on transport failure invalidates the cached base URL and tries once
    /// more against a freshly discovered endpoint. Returns null when Sonar cannot be reached at all.
    /// </summary>
    private async Task<HttpResponseMessage?> SendAsync(
        HttpMethod method, string path, CancellationToken ct, string? jsonBody = null)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var baseUrl = await _discovery.GetBaseUrlAsync(ct).ConfigureAwait(false);
            if (baseUrl is null)
                return null;

            try
            {
                using var request = new HttpRequestMessage(method, baseUrl + path);

                // Built per attempt, never hoisted: an HttpContent cannot be sent twice, so reusing one
                // instance would turn the retry above into an ObjectDisposedException.
                if (jsonBody is not null)
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

                // A 404 after a GG update most likely means the route moved rather than a dead port,
                // so surface it instead of thrashing discovery.
                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                _discovery.Invalidate();
            }
        }

        return null;
    }

    public void Dispose() => _http.Dispose();
}
