using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Core.Models;

namespace Api;

/// <summary>
/// ClientWebSocket-based implementation of <see cref="INotificationsConnection"/>.
///
/// Protocol layer:
///   1. WS upgrade with Bitwarden desktop client headers.
///   2. SignalR MessagePack handshake (send HandshakeBytes + 0x1e, receive ack).
///   3. Binary message loop: accumulate WS fragments, parse via
///      <see cref="SignalRMessagePack.TryParseInvocation"/>, yield matched messages.
/// </summary>
public sealed class NotificationsConnection : INotificationsConnection
{
    private ClientWebSocket? _ws;

    // ----------------------------------------------------------------
    // Static helper (tested by WebSocketUrlTests)
    // ----------------------------------------------------------------

    /// <summary>
    /// Converts a Web Vault URL to the SignalR hub WebSocket URL.
    ///
    /// Rules:
    /// The string overload is retained for callers that only have a Web Vault URL.
    /// New code should pass the resolved notifications endpoint to the typed overload.
    /// </summary>
    public static string ToHubUrl(string serverUrl, string accessToken)
        => ToHubUrl(ServerEndpoints.FromWebVault(serverUrl).Notifications, accessToken);

    /// <summary>
    /// Appends the SignalR hub path to an already resolved notifications endpoint,
    /// converts HTTP(S) to WS(S), and adds the percent-encoded access token.
    /// </summary>
    public static string ToHubUrl(ServerEndpoint notificationsEndpoint, string accessToken)
    {
        ArgumentNullException.ThrowIfNull(accessToken);

        var hub = notificationsEndpoint.Resolve("hub");
        var wsScheme = hub.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            ? "wss"
            : "ws";
        var builder = new UriBuilder(hub)
        {
            Scheme = wsScheme,
            Port = hub.IsDefaultPort ? -1 : hub.Port,
            Query = $"access_token={Uri.EscapeDataString(accessToken)}",
        };
        return builder.Uri.AbsoluteUri;
    }

    // ----------------------------------------------------------------
    // INotificationsConnection
    // ----------------------------------------------------------------

    /// <inheritdoc />
    public async Task ConnectAsync(string notificationsEndpoint, string accessToken, CancellationToken ct)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();

        // Bitwarden client identification headers expected by some server-side logging.
        _ws.Options.SetRequestHeader("Bitwarden-Client-Name", "desktop");
        _ws.Options.SetRequestHeader("Bitwarden-Client-Version", "2026.6.0");

        var endpoint = new ServerEndpoint(notificationsEndpoint);
        await _ws.ConnectAsync(new Uri(ToHubUrl(endpoint, accessToken)), ct)
            .ConfigureAwait(false);

        // ---- SignalR handshake ----
        // Send: HandshakeBytes followed by the 0x1e record separator (Text frame).
        var handshakeBuffer = new byte[SignalRMessagePack.HandshakeBytes.Length + 1];
        SignalRMessagePack.HandshakeBytes.CopyTo(handshakeBuffer, 0);
        handshakeBuffer[^1] = 0x1e;

        await _ws.SendAsync(
            new ArraySegment<byte>(handshakeBuffer),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken: ct).ConfigureAwait(false);

        // Receive the server's handshake acknowledgement.
        // Expected first 3 bytes: {0x7b, 0x7d, 0x1e}  i.e. '{' '}' RS  (empty JSON {}).
        // Accumulate all fragments until EndOfMessage so we handle fragmented frames.
        var ackChunk = new byte[256];
        var ackStream = new System.IO.MemoryStream();
        WebSocketReceiveResult ackResult;
        do
        {
            ackResult = await _ws.ReceiveAsync(new ArraySegment<byte>(ackChunk), ct)
                .ConfigureAwait(false);

            if (ackResult.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException(
                    $"SignalR handshake: server closed the connection unexpectedly ({ackResult.CloseStatus}).");

            ackStream.Write(ackChunk, 0, ackResult.Count);
        }
        while (!ackResult.EndOfMessage);

        var ackBytes = ackStream.ToArray();
        if (ackBytes.Length < 3 || ackBytes[0] != 0x7b || ackBytes[1] != 0x7d || ackBytes[2] != 0x1e)
            throw new InvalidOperationException(
                $"SignalR handshake: unexpected ack bytes: [{string.Join(",", ackBytes.Take(8).Select(b => $"0x{b:x2}"))}]");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<NotificationMessage> ReadAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_ws is null)
            throw new InvalidOperationException("Call ConnectAsync before ReadAsync.");

        // Bound each logical notification frame so a malicious or faulty peer cannot
        // exhaust memory with an oversized or endless fragmented message.
        const int MaxFrameBytes = 1 * 1024 * 1024;

        // We grow this buffer on demand.
        var buf = new byte[4096];
        var accumulated = new System.IO.MemoryStream();

        while (!ct.IsCancellationRequested)
        {
            accumulated.SetLength(0);

            WebSocketReceiveResult result;
            try
            {
                // Accumulate all fragments of one logical message.
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                        yield break;

                    accumulated.Write(buf, 0, result.Count);

                    if (accumulated.Length > MaxFrameBytes)
                        yield break;
                }
                while (!result.EndOfMessage);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (WebSocketException)
            {
                // Remote end closed / network fault → end enumeration gracefully.
                yield break;
            }

            // Only Binary frames carry SignalR MessagePack protocol messages.
            // Text frames (e.g. SignalR JSON handshake remnants or keep-alives) are silently skipped.
            if (result.MessageType != WebSocketMessageType.Binary)
                continue;

            // Try to parse as a SignalR invocation.
            if (accumulated.Length == 0)
                continue;

            var frameBytes = accumulated.ToArray();
            if (SignalRMessagePack.TryParseInvocation(frameBytes, out var msg))
                yield return msg;
            // else: ping, handshake remnant, or unknown → silently skip.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_ws is null) return;

        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Disposing",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort close; ignore errors on disposal.
            }
        }

        _ws.Dispose();
        _ws = null;
    }
}
