using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Core.Models;

/// <summary>
/// Contains the display and routing metadata needed to select a saved account.
/// Authentication and decryption material must never be added to this type.
/// </summary>
public sealed record PersistedAccountMetadata
{
    public PersistedAccountMetadata(
        string accountId,
        string email,
        ServerEndpoints endpoints,
        bool isActive)
    {
        var binding = new PersistedAccountBinding(accountId, endpoints);
        if (string.IsNullOrWhiteSpace(email)
            || email.Length > 320
            || !string.Equals(email, email.Trim(), StringComparison.Ordinal)
            || email.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Email metadata must contain 1 to 320 non-control characters without surrounding whitespace.",
                nameof(email));
        }

        AccountId = binding.AccountId;
        Email = email;
        Endpoints = binding.Endpoints;
        IsActive = isActive;
    }

    public string AccountId { get; }

    public string Email { get; }

    public ServerEndpoints Endpoints { get; }

    public bool IsActive { get; }

    public static PersistedAccountMetadata FromSession(
        PersistedSession session,
        bool isActive)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.TryMigrate(out var normalized))
            throw new InvalidDataException("Persisted session binding is invalid.");
        return new PersistedAccountMetadata(
            normalized.AccountId,
            normalized.Email,
            normalized.Endpoints!,
            isActive);
    }
}

/// <summary>
/// Provides a stable opaque identity for one account and complete server
/// environment. Email is intentionally excluded because it may change.
/// </summary>
public sealed record PersistedAccountBinding
{
    public const int MaximumAccountIdLength = 256;
    public const int MaximumEndpointLength = 2048;

    private const string DerivationDomain = "WinVaultWarden.PersistedAccountBinding.v1";
    private const string IdentifierPurpose = "binding-id";
    private const string BlobEntropyPurpose = "session-blob-entropy";

    public PersistedAccountBinding(string accountId, ServerEndpoints endpoints)
    {
        AccountId = ValidateAccountId(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);
        Endpoints = ValidateEndpoints(endpoints);
    }

    public string AccountId { get; }

    public ServerEndpoints Endpoints { get; }

    public string CreateIdentifier()
    {
        var digest = DeriveDigest(IdentifierPurpose, revision: null);
        try
        {
            return Convert.ToHexStringLower(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    /// <summary>The caller must clear the returned entropy buffer after use.</summary>
    public byte[] CreateBlobProtectionEntropy(Guid revision)
    {
        if (revision == Guid.Empty)
            throw new ArgumentException("Blob revision must not be empty.", nameof(revision));
        return DeriveDigest(BlobEntropyPurpose, revision);
    }

    public string CreateBlobFileName(Guid revision)
    {
        if (revision == Guid.Empty)
            throw new ArgumentException("Blob revision must not be empty.", nameof(revision));
        return $"{CreateIdentifier()}.{revision:N}.bin";
    }

    private byte[] DeriveDigest(string purpose, Guid? revision)
    {
        var values = revision is { } value
            ? new[]
            {
                DerivationDomain,
                purpose,
                AccountId,
                Endpoints.WebVault.Value,
                Endpoints.Api.Value,
                Endpoints.Identity.Value,
                Endpoints.Notifications.Value,
                Endpoints.Icons.Value,
                Endpoints.SendWeb.Value,
                value.ToString("N"),
            }
            : new[]
            {
                DerivationDomain,
                purpose,
                AccountId,
                Endpoints.WebVault.Value,
                Endpoints.Api.Value,
                Endpoints.Identity.Value,
                Endpoints.Notifications.Value,
                Endpoints.Icons.Value,
                Endpoints.SendWeb.Value,
            };

        var byteCounts = new int[values.Length];
        var totalLength = 0;
        for (var index = 0; index < values.Length; index++)
        {
            byteCounts[index] = Encoding.UTF8.GetByteCount(values[index]);
            totalLength = checked(totalLength + sizeof(int) + byteCounts[index]);
        }

        var canonical = GC.AllocateUninitializedArray<byte>(totalLength);
        try
        {
            var offset = 0;
            for (var index = 0; index < values.Length; index++)
            {
                BinaryPrimitives.WriteInt32BigEndian(
                    canonical.AsSpan(offset, sizeof(int)),
                    byteCounts[index]);
                offset += sizeof(int);
                offset += Encoding.UTF8.GetBytes(
                    values[index],
                    canonical.AsSpan(offset, byteCounts[index]));
            }
            return SHA256.HashData(canonical);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            Array.Clear(byteCounts);
        }
    }

    private static string ValidateAccountId(string? accountId)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        if (accountId.Length is 0 or > MaximumAccountIdLength
            || !string.Equals(accountId, accountId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Account identifier must contain 1 to {MaximumAccountIdLength} characters without surrounding whitespace.",
                nameof(accountId));
        }

        for (var index = 0; index < accountId.Length; index++)
        {
            var character = accountId[index];
            if (char.IsControl(character))
                throw new ArgumentException("Account identifier must not contain control characters.", nameof(accountId));
            if (!char.IsSurrogate(character))
                continue;
            if (!char.IsHighSurrogate(character)
                || index + 1 >= accountId.Length
                || !char.IsLowSurrogate(accountId[index + 1]))
            {
                throw new ArgumentException("Account identifier contains invalid Unicode.", nameof(accountId));
            }
            index++;
        }

        return accountId;
    }

    private static ServerEndpoints ValidateEndpoints(ServerEndpoints endpoints)
    {
        var values = new[]
        {
            endpoints.WebVault.Value,
            endpoints.Api.Value,
            endpoints.Identity.Value,
            endpoints.Notifications.Value,
            endpoints.Icons.Value,
            endpoints.SendWeb.Value,
        };
        if (values.Any(value => value.Length > MaximumEndpointLength))
        {
            throw new ArgumentException(
                $"Each server endpoint must not exceed {MaximumEndpointLength} characters.",
                nameof(endpoints));
        }
        return endpoints;
    }
}
