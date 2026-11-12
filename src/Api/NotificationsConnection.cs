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
///   3. Binary message loop: accumulate WS fragments, split every length-prefixed
///      HubMessage, and yield matched notifications in wire order.
/// </summary>
public sealed class NotificationsConnection : INotificationsConnection
{
    internal const int MaxHandshakeResponseSize = 16 * 1024;
    private static readonly byte[] SuccessfulHandshakeResponse = [0x7b, 0x7d, 0x1e];

    private readonly object _connectionGate = new();
    private readonly TimeSpan _closeTimeout;
    private ClientWebSocket? _ws;
    private Task? _disposal;

    public NotificationsConnection() : this(TimeSpan.FromSeconds(2))
    {
    }

    internal NotificationsConnection(TimeSpan closeTimeout)
    {
        if (closeTimeout <= TimeSpan.Zero || closeTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(closeTimeout));
        _closeTimeout = closeTimeout;
    }

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
        ClientWebSocket socket;
        lock (_connectionGate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            _ws?.Dispose();
            _ws = socket = new ClientWebSocket();
        }

        // Bitwarden client identification headers expected by some server-side logging.
        socket.Options.SetRequestHeader("Bitwarden-Client-Name", ClientHeadersHandler.ClientName);
        socket.Options.SetRequestHeader("Bitwarden-Client-Version", ClientHeadersHandler.ClientVersion);

        var endpoint = new ServerEndpoint(notificationsEndpoint);
        await socket.ConnectAsync(new Uri(ToHubUrl(endpoint, accessToken)), ct)
            .ConfigureAwait(false);

        // ---- SignalR handshake ----
        // Send: HandshakeBytes followed by the 0x1e record separator (Text frame).
        var handshakeBuffer = new byte[SignalRMessagePack.HandshakeBytes.Length + 1];
        SignalRMessagePack.HandshakeBytes.CopyTo(handshakeBuffer, 0);
        handshakeBuffer[^1] = 0x1e;

        await socket.SendAsync(
            new ArraySegment<byte>(handshakeBuffer),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken: ct).ConfigureAwait(false);

        // Receive the server's handshake acknowledgement.
        // The complete response must be {0x7b, 0x7d, 0x1e}, i.e. the empty JSON object plus RS.
        // Accumulate all fragments until EndOfMessage so we handle fragmented frames.
        var ackChunk = new byte[256];
        using var ackStream = new MemoryStream();
        WebSocketMessageType? ackMessageType = null;
        WebSocketReceiveResult ackResult;
        do
        {
            ackResult = await socket.ReceiveAsync(new ArraySegment<byte>(ackChunk), ct)
                .ConfigureAwait(false);

            if (ackResult.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException(
                    $"SignalR handshake: server closed the connection unexpectedly ({ackResult.CloseStatus}).");

            AppendHandshakeFragment(
                ackStream,
                ackResult.MessageType,
                ackChunk.AsSpan(0, ackResult.Count),
                ackMessageType);
            ackMessageType ??= ackResult.MessageType;
        }
        while (!ackResult.EndOfMessage);

        ValidateHandshakeAcknowledgement(ackStream.ToArray());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<NotificationMessage> ReadAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var socket = Volatile.Read(ref _ws)
            ?? throw new InvalidOperationException("Call ConnectAsync before ReadAsync.");

        // Accumulate one complete WebSocket message within the protocol receive limit.
        var buf = new byte[4096];
        using var accumulated = new MemoryStream();

        while (!ct.IsCancellationRequested)
        {
            accumulated.SetLength(0);

            WebSocketReceiveResult result;
            try
            {
                // Accumulate all fragments of one logical message.
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buf), ct)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                        yield break;

                    if (result.Count > SignalRMessagePack.MaxReceiveMessageSize - accumulated.Length)
                        throw new InvalidDataException(
                            $"SignalR binary message exceeds the {SignalRMessagePack.MaxReceiveMessageSize}-byte receive limit.");

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
            catch (ObjectDisposedException)
            {
                yield break;
            }

            // Only Binary frames carry SignalR MessagePack protocol messages.
            // Text frames (e.g. SignalR JSON handshake remnants or keep-alives) are silently skipped.
            if (result.MessageType != WebSocketMessageType.Binary)
                continue;

            if (accumulated.Length == 0)
                continue;

            if (!SignalRMessagePack.TryParseInvocations(
                    accumulated.ToArray(),
                    out var notifications))
                throw new InvalidDataException(
                    "SignalR binary message contains an invalid or incomplete length-prefixed HubMessage.");

            foreach (var notification in notifications)
                yield return notification;
        }
    }

    internal static void AppendHandshakeFragment(
        MemoryStream destination,
        WebSocketMessageType messageType,
        ReadOnlySpan<byte> fragment,
        WebSocketMessageType? expectedMessageType = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (messageType is not (
            WebSocketMessageType.Text or WebSocketMessageType.Binary))
            throw new InvalidDataException(
                $"SignalR handshake response must be text or binary, not {messageType}.");
        if (expectedMessageType is not null
            && messageType != expectedMessageType)
        {
            throw new InvalidDataException(
                "SignalR handshake response fragments changed message type.");
        }
        if (fragment.Length > MaxHandshakeResponseSize - destination.Length)
            throw new InvalidDataException(
                $"SignalR handshake response exceeds the {MaxHandshakeResponseSize}-byte limit.");

        destination.Write(fragment);
    }

    internal static void ValidateHandshakeAcknowledgement(ReadOnlySpan<byte> response)
    {
        if (!response.SequenceEqual(SuccessfulHandshakeResponse))
            throw new InvalidDataException(
                $"SignalR handshake: unexpected acknowledgement bytes: [{string.Join(",", response[..Math.Min(response.Length, 8)].ToArray().Select(value => $"0x{value:x2}"))}]");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_connectionGate)
        {
            if (_disposal is null)
            {
                var socket = _ws;
                _ws = null;
                _disposal = socket is null
                    ? Task.CompletedTask
                    : CloseAndDisposeAsync(socket);
            }
            return new ValueTask(_disposal);
        }
    }

    private async Task CloseAndDisposeAsync(ClientWebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // A failed SignalR message can leave the transport open. Never let a peer
                // withholding its close reply block notification shutdown or vault locking.
                using var deadline = new CancellationTokenSource(_closeTimeout);
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Disposing",
                    deadline.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Graceful closure is best effort. Abort terminates any pending transport work.
            socket.Abort();
        }
        finally
        {
            socket.Dispose();
        }
    }
}
