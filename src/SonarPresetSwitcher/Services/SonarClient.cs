using System.Net.Http;
using System.Text.Json;
using SonarPresetSwitcher.Models;

namespace SonarPresetSwitcher.Services;

/// <summary>
/// Thin wrapper over Sonar's undocumented local REST API.
///
///   GET  /configs                 all presets, each carrying isFavorite / favoritePosition
///                                 (see <see cref="GetGamePresetsAsync"/>)
///   GET  /configs/selected        the active preset per channel
///   PUT  /configs/{id}/select     switch the channel that preset belongs to
///   GET  /mode                    cheap liveness probe
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
    private async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var baseUrl = await _discovery.GetBaseUrlAsync(ct).ConfigureAwait(false);
            if (baseUrl is null)
                return null;

            try
            {
                using var request = new HttpRequestMessage(method, baseUrl + path);
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
