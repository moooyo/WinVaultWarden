namespace Core.Models;

/// <summary>
/// Decoded SignalR "ReceiveMessage" push notification from Vaultwarden.
/// </summary>
/// <param name="Type">Notification type integer (matches Bitwarden PushType enum).</param>
/// <param name="EntityId">The affected entity's Id, if present in the payload.</param>
public sealed record NotificationMessage(int Type, string? EntityId);
