namespace Core.Models;

/// <summary>
/// Decoded SignalR "ReceiveMessage" push notification.
/// </summary>
/// <param name="Type">Notification type integer (matches Bitwarden PushType enum).</param>
/// <param name="EntityId">The affected entity's Id, if present in the payload.</param>
/// <param name="ContextId">The originating app or device context, if supplied.</param>
/// <param name="UserId">The payload user identifier, if supplied.</param>
/// <param name="OrganizationId">The payload organization identifier, if supplied.</param>
/// <param name="CollectionIds">Collection identifiers carried by cipher notifications.</param>
/// <param name="RevisionDate">The entity revision timestamp, if supplied.</param>
/// <param name="Date">The user notification timestamp, if supplied.</param>
/// <param name="Enabled">The organization enabled state, if supplied.</param>
/// <param name="LimitCollectionCreation">The collection-creation policy state, if supplied.</param>
/// <param name="LimitCollectionDeletion">The collection-deletion policy state, if supplied.</param>
/// <param name="LimitItemDeletion">The item-deletion policy state, if supplied.</param>
/// <param name="LogoutReason">The server logout reason, if supplied.</param>
public sealed record NotificationMessage(
    int Type,
    string? EntityId,
    string? ContextId = null,
    string? UserId = null,
    string? OrganizationId = null,
    IReadOnlyList<string>? CollectionIds = null,
    DateTimeOffset? RevisionDate = null,
    DateTimeOffset? Date = null,
    bool? Enabled = null,
    bool? LimitCollectionCreation = null,
    bool? LimitCollectionDeletion = null,
    bool? LimitItemDeletion = null,
    int? LogoutReason = null);
