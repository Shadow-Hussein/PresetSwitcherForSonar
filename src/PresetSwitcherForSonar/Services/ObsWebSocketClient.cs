using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PresetSwitcherForSonar.Services;

/// <summary>
/// The least of obs-websocket v5 that <see cref="ObsAudioService"/> needs: the Hello/Identify
/// handshake and request/response, over the BCL's <see cref="ClientWebSocket"/>.
///
/// One instance is one connection, opened for a single operation and disposed after it. Nothing here
/// is long-lived on purpose — the app has no use for OBS's events (the Identify asks for none), and a
/// held socket would be one more thing to notice dying when OBS exits.
/// </summary>
internal sealed class ObsWebSocketClient : IAsyncDisposable
{
    private const int OpHello = 0;
    private const int OpIdentify = 1;
    private const int OpIdentified = 2;
    private const int OpRequest = 6;
    private const int OpRequestResponse = 7;

    /// <summary>The close code obs-websocket uses for a wrong or missing password.</summary>
    private const int CloseAuthenticationFailed = 4009;

    private readonly ClientWebSocket _socket = new();
    private int _nextRequestId;

    private ObsWebSocketClient()
    {
    }

    public static async Task<ObsWebSocketClient> ConnectAsync(int port, string password, CancellationToken ct)
    {
        var client = new ObsWebSocketClient();
        try
        {
            await client._socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), ct).ConfigureAwait(false);

            var hello = await client.ReceiveAsync(ct).ConfigureAwait(false);
            if ((int?)hello["op"] != OpHello)
                throw new ObsException("OBS did not open with the expected obs-websocket greeting.");

            // No event subscriptions: every message after the handshake is then a response to a
            // request of ours, which is what lets RequestAsync read exactly one message back.
            var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };

            if (hello["d"]?["authentication"] is JsonObject auth)
            {
                identify["authentication"] = Authenticate(
                    password, (string?)auth["salt"] ?? "", (string?)auth["challenge"] ?? "");
            }

            await client.SendAsync(OpIdentify, identify, ct).ConfigureAwait(false);

            var identified = await client.ReceiveAsync(ct).ConfigureAwait(false);
            if ((int?)identified["op"] != OpIdentified)
                throw new ObsException("OBS did not accept the obs-websocket handshake.");

            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ObsResponse> RequestAsync(string type, JsonObject? data, CancellationToken ct)
    {
        var id = (++_nextRequestId).ToString();

        var request = new JsonObject { ["requestType"] = type, ["requestId"] = id };
        if (data is not null)
            request["requestData"] = data;

        await SendAsync(OpRequest, request, ct).ConfigureAwait(false);

        while (true)
        {
            var message = await ReceiveAsync(ct).ConfigureAwait(false);
            if ((int?)message["op"] != OpRequestResponse || (string?)message["d"]?["requestId"] != id)
                continue;

            var status = message["d"]?["requestStatus"];

            return new ObsResponse(
                type,
                (bool?)status?["result"] ?? false,
                (int?)status?["code"] ?? 0,
                (string?)status?["comment"],
                message["d"]?["responseData"] as JsonObject);
        }
    }

    /// <summary>base64(sha256(base64(sha256(password + salt)) + challenge)), per the protocol.</summary>
    private static string Authenticate(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private Task SendAsync(int op, JsonObject data, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["op"] = op, ["d"] = data });
        return _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private async Task<JsonNode> ReceiveAsync(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var result = await _socket.ReceiveAsync(chunk, ct).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new ObsException((int?)result.CloseStatus == CloseAuthenticationFailed
                    ? "OBS rejected the WebSocket password."
                    : $"OBS closed the connection ({(int?)result.CloseStatus}: {result.CloseStatusDescription}).");
            }

            buffer.Write(chunk, 0, result.Count);

            if (result.EndOfMessage)
                break;
        }

        return JsonNode.Parse(buffer.ToArray())
               ?? throw new ObsException("OBS sent an empty obs-websocket message.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            // Closing politely is a courtesy; the socket is disposed either way.
        }

        _socket.Dispose();
    }
}

/// <summary>One obs-websocket request's outcome. <see cref="Code"/> is OBS's RequestStatus.</summary>
internal sealed record ObsResponse(string Type, bool Ok, int Code, string? Comment, JsonObject? Data)
{
    /// <summary>OBS is still starting — its scene collection is not loaded yet.</summary>
    public const int NotReady = 207;

    /// <summary>The response data, or an <see cref="ObsException"/> when OBS refused the request.</summary>
    public JsonObject Expect() => Ok
        ? Data ?? new JsonObject()
        : throw new ObsException($"OBS refused {Type} ({Code}{(Comment is null ? "" : $": {Comment}")}).", Code);
}

internal sealed class ObsException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// OBS's own WebSocket server settings, read from the file OBS keeps them in. That is where the port
/// and password come from: asking the user to type them in again would mean storing a second copy of
/// a password in our settings.json for no gain. Read only, and re-read on every connect, so a change
/// made in OBS's dialog is picked up without a restart of either app.
/// </summary>
internal sealed record ObsServerConfig(bool Enabled, int Port, bool AuthRequired, string Password)
{
    private const int DefaultPort = 4455;

    /// <summary>Null when the file is missing or unreadable — OBS writes it on first run.</summary>
    public static ObsServerConfig? Read()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "obs-studio", "plugin_config", "obs-websocket", "config.json");

        try
        {
            if (!File.Exists(path))
                return null;

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
                return null;

            return new ObsServerConfig(
                root["server_enabled"]?.GetValue<bool>() ?? false,
                root["server_port"]?.GetValue<int>() ?? DefaultPort,
                root["auth_required"]?.GetValue<bool>() ?? false,
                root["server_password"]?.GetValue<string>() ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                       InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
