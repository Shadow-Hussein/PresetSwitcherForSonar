using System.IO;
using System.Net.Http;
using System.Text.Json;
using PresetSwitcherForSonar.Models;

namespace PresetSwitcherForSonar.Services;

/// <summary>
/// Resolves the Sonar sub-app's HTTP base URL. The port is assigned fresh every time SteelSeries GG
/// launches, so it must be discovered at runtime and re-discovered whenever a call fails.
///
/// Chain: coreProps.json -> ggEncryptedAddress -> GET https://host:port/subApps -> sonar.metadata.webServerAddress
/// </summary>
public sealed class SonarDiscovery : IDisposable
{
    private static readonly string[] CorePropsPaths =
    [
        @"C:\ProgramData\SteelSeries\GG\coreProps.json",
        @"C:\ProgramData\SteelSeries\SteelSeries Engine 3\coreProps.json",
    ];

    private readonly HttpClient _gg;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cachedBaseUrl;

    public SonarDiscovery()
    {
        // The GG control server presents a self-signed SteelSeries certificate. This handler is used
        // only for the loopback discovery call, never for anything that leaves the machine.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        _gg = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>Last successfully resolved base URL, or null. Does not trigger discovery.</summary>
    public string? CachedBaseUrl => _cachedBaseUrl;

    /// <summary>Forces the next <see cref="GetBaseUrlAsync"/> to re-run the full discovery chain.</summary>
    public void Invalidate() => _cachedBaseUrl = null;

    /// <summary>
    /// Returns the Sonar base URL (e.g. <c>http://127.0.0.1:50453</c>), or null when GG or the Sonar
    /// sub-app is not running.
    /// </summary>
    public async Task<string?> GetBaseUrlAsync(CancellationToken ct = default)
    {
        var cached = _cachedBaseUrl;
        if (cached is not null)
            return cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another caller may have resolved it while we waited.
            if (_cachedBaseUrl is not null)
                return _cachedBaseUrl;

            var resolved = await ResolveAsync(ct).ConfigureAwait(false);
            _cachedBaseUrl = resolved;
            return resolved;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> ResolveAsync(CancellationToken ct)
    {
        var ggAddress = ReadGgAddress();
        if (string.IsNullOrWhiteSpace(ggAddress))
            return null;

        SubAppsResponse? subApps;
        try
        {
            await using var stream = await _gg.GetStreamAsync($"https://{ggAddress}/subApps", ct).ConfigureAwait(false);
            subApps = await JsonSerializer.DeserializeAsync<SubAppsResponse>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }

        if (subApps?.SubApps is null || !subApps.SubApps.TryGetValue("sonar", out var sonar))
            return null;

        if (!sonar.IsRunning || !sonar.IsReady)
            return null;

        var address = sonar.Metadata?.WebServerAddress;
        return string.IsNullOrWhiteSpace(address) ? null : address.TrimEnd('/');
    }

    private static string? ReadGgAddress()
    {
        foreach (var path in CorePropsPaths)
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var props = JsonSerializer.Deserialize<CoreProps>(File.ReadAllText(path));
                if (!string.IsNullOrWhiteSpace(props?.GgEncryptedAddress))
                    return props.GgEncryptedAddress;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Try the next known location.
            }
        }

        return null;
    }

    public void Dispose()
    {
        _gg.Dispose();
        _gate.Dispose();
    }
}
