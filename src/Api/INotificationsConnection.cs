using Core.Models;

namespace Api;

/// <summary>
/// Represents a live connection to the Vaultwarden SignalR notifications hub.
/// Implementations handle the WebSocket transport, SignalR handshake, and
/// the binary MessagePack message framing layer.
/// </summary>
public interface INotificationsConnection : IAsyncDisposable
{
    /// <summary>
    /// Opens the WebSocket, performs the SignalR MessagePack handshake, and
    /// prepares the connection to receive messages.
    /// </summary>
    /// <param name="notificationsEndpoint">Resolved notifications service endpoint.</param>
    /// <param name="accessToken">Bearer access token to authenticate the hub connection.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ConnectAsync(string notificationsEndpoint, string accessToken, CancellationToken ct);

    /// <summary>
    /// Asynchronously yields decoded <see cref="NotificationMessage"/> values until
    /// the connection closes or <paramref name="ct"/> is cancelled.
    /// Pings, handshake noise, and unrecognised frames are silently skipped.
    /// </summary>
    IAsyncEnumerable<NotificationMessage> ReadAsync(CancellationToken ct);
}
