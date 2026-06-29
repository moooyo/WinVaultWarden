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
    /// Converts a Vaultwarden base URL to the SignalR hub WebSocket URL.
    ///
    /// Rules:
    ///   • http  → ws   (https → wss)
    ///   • path  = /notifications/hub
    ///   • query = access_token=&lt;percent-encoded token&gt;
    ///   • Non-default ports are always preserved in the output string.
    ///
    /// UriBuilder normalises default ports (80/443) away and may reorder query.
    /// To avoid those quirks we build the string manually after extracting host
    /// and port from the input URI.
    /// </summary>
    public static string ToHubUrl(string serverUrl, string accessToken)
    {
        var uri = new Uri(serverUrl);

        // Determine the WebSocket scheme.
        var wsScheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            ? "wss"
            : "ws";

        // Always include the port explicitly when it differs from the scheme default,
        // but also preserve it when the caller supplies a non-standard port.
        // uri.Port already returns the effective port (e.g. 80 for http when omitted).
        bool isDefaultPort =
            (wsScheme == "ws"  && uri.Port == 80)  ||
            (wsScheme == "wss" && uri.Port == 443);

        var hostPart = isDefaultPort
            ? uri.Host
            : $"{uri.Host}:{uri.Port}";

        var encodedToken = Uri.EscapeDataString(accessToken);

        return $"{wsScheme}://{hostPart}/notifications/hub?access_token={encodedToken}";
    }

    // ----------------------------------------------------------------
    // INotificationsConnection
    // ----------------------------------------------------------------

    /// <inheritdoc />
    public async Task ConnectAsync(string serverUrl, string accessToken, CancellationToken ct)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();

        // Bitwarden client identification headers expected by some server-side logging.
        _ws.Options.SetRequestHeader("Bitwarden-Client-Name", "desktop");
        _ws.Options.SetRequestHeader("Bitwarden-Client-Version", "2026.6.0");

        await _ws.ConnectAsync(new Uri(ToHubUrl(serverUrl, accessToken)), ct)
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
