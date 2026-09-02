namespace Core.Models;

public sealed record VaultOrganization(
    string Id,
    string Name,
    int Status,
    int Type,
    bool Enabled)
{
    public string? EncryptedKey { get; init; }
    public string? ProviderId { get; init; }
    public string? EncryptedProviderKey { get; init; }
    public bool IsMember { get; init; } = true;
    public bool IsProviderUser { get; init; }
    public bool IsProviderOrganization { get; init; }
    public int? ProductTierType { get; init; }
}

public sealed record VaultCollection(
    string Id,
    string OrganizationId,
    string Name,
    bool ReadOnly,
    bool HidePasswords,
    bool Manage,
    string? ExternalId);

public sealed record VaultOrganizationPolicy(
    string Id,
    string OrganizationId,
    int Type,
    bool Enabled,
    string DataJson)
{
    public DateTimeOffset? RevisionDate { get; init; }
    public bool CanToggleState { get; init; } = true;
}

/// <summary>
/// Contains only the membership facts required to decide whether organization policies apply.
/// Accepted memberships belong here but must not become writable vault destinations.
/// </summary>
public sealed record VaultOrganizationPolicyContext(
    string Id,
    int Status,
    int Role,
    bool Enabled,
    bool UsePolicies,
    bool IsProviderUser);
