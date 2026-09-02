namespace Core.Models;

public enum AccountDeletionStage
{
    Prepared,
    ServerConfirmed,
    LocalCleanupComplete,
    UncertainLocalCleanupComplete,
}

/// <summary>
/// Identifies the exact account, server environment, and device for one durable
/// account-deletion attempt.
/// </summary>
public sealed record AccountDeletionBinding
{
    public const int MaximumAccountIdentifierLength = 256;
    public const int MaximumEndpointLength = 2048;

    public AccountDeletionBinding(
        string accountId,
        string email,
        ServerEndpoints endpoints,
        string deviceIdentifier)
    {
        AccountId = RequireIdentifier(
            accountId,
            MaximumAccountIdentifierLength,
            nameof(accountId));
        ArgumentNullException.ThrowIfNull(endpoints);
        if (new[]
            {
                endpoints.WebVault.Value,
                endpoints.Api.Value,
                endpoints.Identity.Value,
                endpoints.Notifications.Value,
                endpoints.Icons.Value,
                endpoints.SendWeb.Value,
            }.Any(value => value.Length > MaximumEndpointLength))
        {
            throw new ArgumentException(
                $"Each server endpoint must not exceed {MaximumEndpointLength} characters.",
                nameof(endpoints));
        }

        var normalizedEmail = MasterPasswordSaltPolicy.NormalizeEmail(email);
        MasterPasswordSaltPolicy.Validate(normalizedEmail);
        if (!HasValidCharacters(normalizedEmail))
        {
            throw new ArgumentException(
                "The normalized email contains invalid characters.",
                nameof(email));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentifier);
        if (!Guid.TryParseExact(deviceIdentifier, "D", out var parsedDeviceIdentifier)
            || parsedDeviceIdentifier == Guid.Empty)
        {
            throw new ArgumentException(
                "The device identifier must be a non-empty GUID in D format.",
                nameof(deviceIdentifier));
        }

        NormalizedEmail = normalizedEmail;
        Endpoints = endpoints;
        DeviceIdentifier = parsedDeviceIdentifier.ToString("D");
    }

    public string AccountId { get; }
    public string NormalizedEmail { get; }
    public ServerEndpoints Endpoints { get; }
    public string DeviceIdentifier { get; }

    private static string RequireIdentifier(
        string? value,
        int maximumLength,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is 0
            || value.Length > maximumLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !HasValidCharacters(value))
        {
            throw new ArgumentException(
                $"The identifier must contain 1 to {maximumLength} valid characters without surrounding whitespace.",
                parameterName);
        }
        return value;
    }

    private static bool HasValidCharacters(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character))
                return false;
            if (!char.IsSurrogate(character))
                continue;
            if (!char.IsHighSurrogate(character)
                || index + 1 >= value.Length
                || !char.IsLowSurrogate(value[index + 1]))
            {
                return false;
            }
            index++;
        }
        return true;
    }
}

/// <summary>
/// Durable, non-secret evidence for an account deletion. Records are retained
/// until an explicit compare-and-swap removal and never expire automatically.
/// </summary>
public sealed record AccountDeletionTombstoneRecord
{
    public AccountDeletionTombstoneRecord(
        Guid entryId,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        AccountDeletionBinding binding,
        AccountDeletionStage stage)
    {
        if (entryId == Guid.Empty)
            throw new ArgumentException("Entry identifier cannot be empty.", nameof(entryId));
        if (revision <= 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        ArgumentNullException.ThrowIfNull(binding);
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage));

        var normalizedCreatedAt = createdAt.ToUniversalTime();
        var normalizedUpdatedAt = updatedAt.ToUniversalTime();
        if (normalizedCreatedAt < DateTimeOffset.UnixEpoch)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                "The creation timestamp cannot precede the Unix epoch.");
        }
        if (normalizedUpdatedAt < normalizedCreatedAt)
        {
            throw new ArgumentException(
                "The update timestamp cannot precede the creation timestamp.",
                nameof(updatedAt));
        }
        if (stage == AccountDeletionStage.Prepared
            && normalizedUpdatedAt != normalizedCreatedAt)
        {
            throw new ArgumentException(
                "A prepared tombstone must have matching creation and update timestamps.",
                nameof(updatedAt));
        }

        EntryId = entryId;
        Revision = revision;
        CreatedAt = normalizedCreatedAt;
        UpdatedAt = normalizedUpdatedAt;
        Binding = binding;
        Stage = stage;
    }

    public Guid EntryId { get; }
    public long Revision { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
    public AccountDeletionBinding Binding { get; }
    public AccountDeletionStage Stage { get; }
}
