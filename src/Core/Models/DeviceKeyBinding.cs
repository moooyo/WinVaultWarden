using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Core.Models;

/// <summary>
/// Identifies the only account, server environment, and device that may use a
/// persisted trusted-device encryption key.
/// </summary>
public sealed record DeviceKeyBinding
{
    public const int MaximumIdentifierLength = 256;

    private const string DerivationDomain = "WinVaultWarden.DeviceKeyBinding.v1";
    private const string EntropyPurpose = "dpapi-entropy";
    private const string StoragePurpose = "storage-file";

    public DeviceKeyBinding(
        string accountId,
        ServerEndpoints endpoints,
        string deviceIdentifier)
    {
        AccountId = ValidateIdentifier(accountId, nameof(accountId));
        ArgumentNullException.ThrowIfNull(endpoints);
        Endpoints = endpoints;
        DeviceIdentifier = ValidateIdentifier(deviceIdentifier, nameof(deviceIdentifier));
    }

    public string AccountId { get; }

    public ServerEndpoints Endpoints { get; }

    public string DeviceIdentifier { get; }

    public ServerEndpoint WebVault => Endpoints.WebVault;

    /// <summary>
    /// Derives binding-specific entropy for an operating-system data protector.
    /// The caller must clear the returned buffer after use.
    /// </summary>
    public byte[] CreateProtectionEntropy() => DeriveDigest(EntropyPurpose);

    /// <summary>
    /// Returns an opaque per-binding file name that does not disclose account
    /// or device identifiers.
    /// </summary>
    public string CreateStorageFileName()
    {
        var digest = DeriveDigest(StoragePurpose);
        try
        {
            return $"{Convert.ToHexStringLower(digest)}.bin";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private byte[] DeriveDigest(string purpose)
    {
        var values = new[]
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
            DeviceIdentifier,
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

    private static string ValidateIdentifier(string? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is 0 or > MaximumIdentifierLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !HasValidCharacters(value))
        {
            throw new ArgumentException(
                $"Identifier must contain 1 to {MaximumIdentifierLength} valid, non-control characters without surrounding whitespace.",
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
