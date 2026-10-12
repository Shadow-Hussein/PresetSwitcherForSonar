using System.Text.Json.Serialization;

namespace PresetSwitcherForSonar.Models;

/// <summary>Shape of <c>GET https://{ggEncryptedAddress}/subApps</c>.</summary>
public sealed class SubAppsResponse
{
    [JsonPropertyName("subApps")]
    public Dictionary<string, SubApp>? SubApps { get; set; }
}

public sealed class SubApp
{
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; }

    [JsonPropertyName("isReady")]
    public bool IsReady { get; set; }

    [JsonPropertyName("isRunning")]
    public bool IsRunning { get; set; }

    [JsonPropertyName("metadata")]
    public SubAppMetadata? Metadata { get; set; }
}

public sealed class SubAppMetadata
{
    /// <summary>For Sonar this is a plain-HTTP origin such as <c>http://127.0.0.1:50453</c>.</summary>
    [JsonPropertyName("webServerAddress")]
    public string? WebServerAddress { get; set; }
}

/// <summary>Shape of <c>coreProps.json</c>.</summary>
public sealed class CoreProps
{
    /// <summary>HTTPS host:port of the GG control server (self-signed cert).</summary>
    [JsonPropertyName("ggEncryptedAddress")]
    public string? GgEncryptedAddress { get; set; }
}
