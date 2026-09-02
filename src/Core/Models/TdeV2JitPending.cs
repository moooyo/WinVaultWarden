using System.Formats.Cbor;
using System.Security.Cryptography;

namespace Core.Models;

public enum TdeV2JitPendingStage
{
    Prepared,
    AccountKeysSubmissionStarted,
    AccountKeysConfirmed,
    ResetPasswordEnrolled,
    TrustedDeviceEnrolled,
    LocalCommitReady,
}

public sealed record TdeV2JitPublicKeyEncryptionKeyPairSnapshot
{
    public TdeV2JitPublicKeyEncryptionKeyPairSnapshot(
        string wrappedPrivateKey,
        string publicKey,
        string signedPublicKey)
    {
        WrappedPrivateKey = TdeV2JitPendingValidation.RequireType7(
            wrappedPrivateKey,
            nameof(wrappedPrivateKey));
        PublicKey = TdeV2JitPendingValidation.RequireCanonicalBase64(
            publicKey,
            nameof(publicKey));
        SignedPublicKey = TdeV2JitPendingValidation.RequireCanonicalBase64(
            signedPublicKey,
            nameof(signedPublicKey));
    }

    public string WrappedPrivateKey { get; }
    public string PublicKey { get; }
    public string SignedPublicKey { get; }
}

public sealed record TdeV2JitSignatureKeyPairSnapshot
{
    public TdeV2JitSignatureKeyPairSnapshot(
        string signatureAlgorithm,
        string wrappedSigningKey,
        string verifyingKey)
    {
        if (!string.Equals(signatureAlgorithm, "mldsa44", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The signature algorithm must be mldsa44.",
                nameof(signatureAlgorithm));
        }

        SignatureAlgorithm = signatureAlgorithm;
        WrappedSigningKey = TdeV2JitPendingValidation.RequireType7(
            wrappedSigningKey,
            nameof(wrappedSigningKey));
        VerifyingKey = TdeV2JitPendingValidation.RequireCanonicalBase64(
            verifyingKey,
            nameof(verifyingKey));
    }

    public string SignatureAlgorithm { get; }
    public string WrappedSigningKey { get; }
    public string VerifyingKey { get; }
}

public sealed record TdeV2JitAccountSecurityStateSnapshot
{
    public TdeV2JitAccountSecurityStateSnapshot(string securityState, int securityVersion)
    {
        SecurityState = TdeV2JitPendingValidation.RequireCanonicalBase64(
            securityState,
            nameof(securityState));
        if (securityVersion != 2)
            throw new ArgumentOutOfRangeException(nameof(securityVersion));
        SecurityVersion = securityVersion;
    }

    public string SecurityState { get; }
    public int SecurityVersion { get; }
}

public sealed record TdeV2JitAccountKeysSnapshot
{
    public TdeV2JitAccountKeysSnapshot(
        string userKeyEncryptedAccountPrivateKey,
        string accountPublicKey,
        TdeV2JitPublicKeyEncryptionKeyPairSnapshot publicKeyEncryptionKeyPair,
        TdeV2JitSignatureKeyPairSnapshot signatureKeyPair,
        TdeV2JitAccountSecurityStateSnapshot securityState)
    {
        UserKeyEncryptedAccountPrivateKey = TdeV2JitPendingValidation.RequireType7(
            userKeyEncryptedAccountPrivateKey,
            nameof(userKeyEncryptedAccountPrivateKey));
        AccountPublicKey = TdeV2JitPendingValidation.RequireRsa2048PublicKey(
            accountPublicKey,
            nameof(accountPublicKey));
        PublicKeyEncryptionKeyPair = publicKeyEncryptionKeyPair
            ?? throw new ArgumentNullException(nameof(publicKeyEncryptionKeyPair));
        SignatureKeyPair = signatureKeyPair
            ?? throw new ArgumentNullException(nameof(signatureKeyPair));
        SecurityState = securityState
            ?? throw new ArgumentNullException(nameof(securityState));

        if (!string.Equals(
                UserKeyEncryptedAccountPrivateKey,
                PublicKeyEncryptionKeyPair.WrappedPrivateKey,
                StringComparison.Ordinal)
            || !string.Equals(
                AccountPublicKey,
                PublicKeyEncryptionKeyPair.PublicKey,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The duplicated account-key request values must match exactly.");
        }
    }

    public string UserKeyEncryptedAccountPrivateKey { get; }
    public string AccountPublicKey { get; }
    public TdeV2JitPublicKeyEncryptionKeyPairSnapshot PublicKeyEncryptionKeyPair { get; }
    public TdeV2JitSignatureKeyPairSnapshot SignatureKeyPair { get; }
    public TdeV2JitAccountSecurityStateSnapshot SecurityState { get; }
}

/// <summary>
/// Exact wire snapshot for one POST /accounts/keys request.
/// </summary>
public sealed record TdeV2JitAccountKeysRequestSnapshot
{
    public TdeV2JitAccountKeysRequestSnapshot(
        string publicKey,
        string encryptedPrivateKey,
        TdeV2JitAccountKeysSnapshot accountKeys,
        string userKeyId)
    {
        PublicKey = TdeV2JitPendingValidation.RequireRsa2048PublicKey(
            publicKey,
            nameof(publicKey));
        EncryptedPrivateKey = TdeV2JitPendingValidation.RequireType7(
            encryptedPrivateKey,
            nameof(encryptedPrivateKey));
        AccountKeys = accountKeys ?? throw new ArgumentNullException(nameof(accountKeys));
        UserKeyId = TdeV2JitPendingValidation.RequireLowerHexKeyId(
            userKeyId,
            nameof(userKeyId));

        if (!string.Equals(PublicKey, AccountKeys.AccountPublicKey, StringComparison.Ordinal)
            || !string.Equals(
                EncryptedPrivateKey,
                AccountKeys.UserKeyEncryptedAccountPrivateKey,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The outer and nested account-key request values must match exactly.");
        }
    }

    public string PublicKey { get; }
    public string EncryptedPrivateKey { get; }
    public TdeV2JitAccountKeysSnapshot AccountKeys { get; }
    public string UserKeyId { get; }
}

/// <summary>
/// Exact wire snapshot for the optional trusted-device registration request.
/// </summary>
public sealed record TdeV2JitTrustedDeviceRequestSnapshot
{
    public TdeV2JitTrustedDeviceRequestSnapshot(
        string encryptedUserKey,
        string encryptedPublicKey,
        string encryptedPrivateKey)
    {
        EncryptedUserKey = TdeV2JitPendingValidation.RequireType4Rsa2048(
            encryptedUserKey,
            nameof(encryptedUserKey));
        EncryptedPublicKey = TdeV2JitPendingValidation.RequireType7(
            encryptedPublicKey,
            nameof(encryptedPublicKey));
        EncryptedPrivateKey = TdeV2JitPendingValidation.RequireLegacyType2(
            encryptedPrivateKey,
            nameof(encryptedPrivateKey));
    }

    public string EncryptedUserKey { get; }
    public string EncryptedPublicKey { get; }
    public string EncryptedPrivateKey { get; }
}

/// <summary>
/// Owns the complete client state required to safely resume one V2 TDE JIT enrollment.
/// </summary>
public sealed class TdeV2JitPending : IDisposable
{
    public const int MaximumEndpointLength = 2048;
    public const int MaximumOrganizationIdentifierLength = 256;
    public const int MaximumWireValueLength = 128 * 1024;
    public const int MaximumUserKeyTransportLength = 1024;
    public const int DeviceKeyLength = 64;

    private readonly object _gate = new();
    private byte[]? _userKeyTransport;
    private byte[]? _deviceKey;

    public TdeV2JitPending(
        DeviceKeyBinding binding,
        string organizationId,
        string organizationIdentifier,
        DateTimeOffset createdAt,
        ReadOnlySpan<byte> userKeyTransport,
        TdeV2JitAccountKeysRequestSnapshot accountKeysRequest,
        string resetPasswordKey,
        TdeV2JitTrustedDeviceRequestSnapshot? trustedDeviceRequest = null,
        ReadOnlySpan<byte> deviceKey = default)
    {
        Binding = NormalizeBinding(binding);
        OrganizationId = TdeV2JitPendingValidation.RequireCanonicalGuid(
            organizationId,
            nameof(organizationId));
        OrganizationIdentifier = TdeV2JitPendingValidation.RequireIdentifier(
            organizationIdentifier,
            MaximumOrganizationIdentifierLength,
            nameof(organizationIdentifier));
        CreatedAt = createdAt.ToUniversalTime();
        if (CreatedAt < DateTimeOffset.UnixEpoch)
            throw new ArgumentOutOfRangeException(nameof(createdAt));

        AccountKeysRequest = accountKeysRequest
            ?? throw new ArgumentNullException(nameof(accountKeysRequest));
        ResetPasswordKey = TdeV2JitPendingValidation.RequireType4Rsa2048(
            resetPasswordKey,
            nameof(resetPasswordKey));
        TrustedDeviceRequest = trustedDeviceRequest;
        if ((trustedDeviceRequest is null) != deviceKey.IsEmpty)
        {
            throw new ArgumentException(
                "Trusted-device request fields and the device key must either both be present or both be absent.",
                nameof(deviceKey));
        }

        byte[]? ownedUserKeyTransport = userKeyTransport.ToArray();
        byte[]? ownedDeviceKey = deviceKey.IsEmpty ? null : deviceKey.ToArray();
        try
        {
            ValidateUserKeyTransport(
                ownedUserKeyTransport,
                AccountKeysRequest.UserKeyId);
            if (ownedDeviceKey is not null && ownedDeviceKey.Length != DeviceKeyLength)
            {
                throw new ArgumentException(
                    "A trusted-device key must contain exactly 64 bytes.",
                    nameof(deviceKey));
            }

            _userKeyTransport = ownedUserKeyTransport;
            ownedUserKeyTransport = null;
            _deviceKey = ownedDeviceKey;
            ownedDeviceKey = null;
        }
        finally
        {
            Clear(ownedDeviceKey);
            Clear(ownedUserKeyTransport);
        }
    }

    public DeviceKeyBinding Binding { get; }
    public string AccountId => Binding.AccountId;
    public ServerEndpoints Endpoints => Binding.Endpoints;
    public string DeviceIdentifier => Binding.DeviceIdentifier;
    public string OrganizationId { get; }
    public string OrganizationIdentifier { get; }
    public DateTimeOffset CreatedAt { get; }
    public TdeV2JitAccountKeysRequestSnapshot AccountKeysRequest { get; }
    public string ResetPasswordKey { get; }
    public TdeV2JitTrustedDeviceRequestSnapshot? TrustedDeviceRequest { get; }
    public bool TrustCurrentDevice => TrustedDeviceRequest is not null;

    /// <summary>
    /// Returns a caller-owned V2 user-key transport. The caller must zero it after use.
    /// </summary>
    public byte[] CopyUserKeyTransport()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_userKeyTransport is null, this);
            return _userKeyTransport.ToArray();
        }
    }

    /// <summary>
    /// Returns a caller-owned device-key copy, or null when device trust was not requested.
    /// The caller must zero a returned copy after use.
    /// </summary>
    public byte[]? CopyDeviceKey()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_userKeyTransport is null, this);
            return _deviceKey?.ToArray();
        }
    }

    public TdeV2JitPending Clone()
    {
        var userKeyTransport = CopyUserKeyTransport();
        var deviceKey = CopyDeviceKey();
        try
        {
            return new TdeV2JitPending(
                Binding,
                OrganizationId,
                OrganizationIdentifier,
                CreatedAt,
                userKeyTransport,
                AccountKeysRequest,
                ResetPasswordKey,
                TrustedDeviceRequest,
                deviceKey ?? ReadOnlySpan<byte>.Empty);
        }
        finally
        {
            Clear(deviceKey);
            Clear(userKeyTransport);
        }
    }

    public void Dispose()
    {
        byte[]? userKeyTransport;
        byte[]? deviceKey;
        lock (_gate)
        {
            userKeyTransport = _userKeyTransport;
            deviceKey = _deviceKey;
            _userKeyTransport = null;
            _deviceKey = null;
        }
        Clear(deviceKey);
        Clear(userKeyTransport);
    }

    private static DeviceKeyBinding NormalizeBinding(DeviceKeyBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var accountId = TdeV2JitPendingValidation.RequireCanonicalGuid(
            binding.AccountId,
            nameof(binding));
        var deviceIdentifier = TdeV2JitPendingValidation.RequireCanonicalGuid(
            binding.DeviceIdentifier,
            nameof(binding));
        var endpoints = binding.Endpoints;
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
                "Each server endpoint must not exceed 2048 characters.",
                nameof(binding));
        }

        return new DeviceKeyBinding(accountId, endpoints, deviceIdentifier);
    }

    private static void ValidateUserKeyTransport(
        byte[] userKeyTransport,
        string expectedKeyId)
    {
        if (userKeyTransport.Length is <= 64 or > MaximumUserKeyTransportLength)
        {
            throw new ArgumentException(
                "The V2 user-key transport length is invalid.",
                nameof(userKeyTransport));
        }

        byte[]? encoded = null;
        byte[]? keyId = null;
        byte[]? key = null;
        try
        {
            var paddingLength = userKeyTransport[^1];
            if (paddingLength == 0 || paddingLength > userKeyTransport.Length)
                throw new FormatException("The V2 user-key transport padding is invalid.");
            var mismatch = 0;
            foreach (var value in userKeyTransport.AsSpan()[^paddingLength..])
                mismatch |= value ^ paddingLength;
            if (mismatch != 0)
                throw new FormatException("The V2 user-key transport padding is invalid.");

            encoded = userKeyTransport.AsSpan()[..^paddingLength].ToArray();
            var reader = new CborReader(
                encoded,
                CborConformanceMode.Canonical,
                allowMultipleRootLevelValues: false);
            if (reader.ReadStartMap() != 5)
                throw new FormatException("The V2 user key must contain exactly five fields.");

            int? keyType = null;
            long? algorithm = null;
            HashSet<int>? operations = null;
            var labels = new HashSet<long>();
            for (var index = 0; index < 5; index++)
            {
                var label = reader.ReadInt64();
                if (!labels.Add(label))
                    throw new FormatException("The V2 user key contains a duplicate field.");
                switch (label)
                {
                    case 1:
                        keyType = checked((int)reader.ReadInt64());
                        break;
                    case 2:
                        keyId = reader.ReadByteString();
                        break;
                    case 3:
                        algorithm = reader.ReadInt64();
                        break;
                    case 4:
                    {
                        var count = reader.ReadStartArray();
                        if (count != 4)
                            throw new FormatException("The V2 user key operations are incomplete.");
                        operations = [];
                        for (var operationIndex = 0; operationIndex < count.Value; operationIndex++)
                            operations.Add(checked((int)reader.ReadInt64()));
                        reader.ReadEndArray();
                        break;
                    }
                    case -1:
                        key = reader.ReadByteString();
                        break;
                    default:
                        throw new FormatException("The V2 user key contains an unsupported field.");
                }
            }
            reader.ReadEndMap();
            if (reader.BytesRemaining != 0 || reader.PeekState() != CborReaderState.Finished)
                throw new FormatException("The V2 user key contains trailing data.");

            if (keyType != 4
                || algorithm != -70010
                || keyId is not { Length: 16 }
                || key is not { Length: 32 }
                || operations is null
                || !operations.SetEquals([3, 4, 5, 6])
                || !string.Equals(
                    Convert.ToHexStringLower(keyId),
                    expectedKeyId,
                    StringComparison.Ordinal))
            {
                throw new FormatException(
                    "The V2 user-key transport does not match the account-keys request.");
            }
        }
        catch (Exception exception) when (exception is
            CborContentException
                or FormatException
                or InvalidOperationException
                or OverflowException)
        {
            throw new ArgumentException(
                "The V2 user-key transport is invalid or does not match its request.",
                nameof(userKeyTransport),
                exception);
        }
        finally
        {
            Clear(key);
            Clear(keyId);
            Clear(encoded);
        }
    }

    private static void Clear(byte[]? bytes)
    {
        if (bytes is not null)
            CryptographicOperations.ZeroMemory(bytes);
    }
}

internal static class TdeV2JitPendingValidation
{
    public static string RequireCanonicalGuid(string? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        return Guid.TryParseExact(value, "D", out var parsed)
            && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal)
                ? value
                : throw new ArgumentException(
                    "The identifier must be a canonical GUID.",
                    parameterName);
    }

    public static string RequireIdentifier(
        string? value,
        int maximumLength,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is 0 || value.Length > maximumLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !HasValidCharacters(value))
        {
            throw new ArgumentException(
                $"The identifier must contain 1 to {maximumLength} non-control characters without surrounding whitespace.",
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

    public static string RequireLowerHexKeyId(string? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 32
            || value.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The V2 user key identifier must be 32 lowercase hexadecimal characters.",
                parameterName);
        }
        return value;
    }

    public static string RequireRsa2048PublicKey(string? value, string parameterName)
    {
        var canonical = RequireCanonicalBase64(value, parameterName);
        var decoded = Convert.FromBase64String(canonical);
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(decoded, out var bytesRead);
            if (bytesRead != decoded.Length || rsa.KeySize != 2048)
                throw new CryptographicException("The public key is not RSA-2048 SPKI.");
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException(
                "The public key must be an exact RSA-2048 SPKI value.",
                parameterName,
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
        return canonical;
    }

    public static string RequireCanonicalBase64(string? value, string parameterName)
    {
        var bounded = RequireWireValue(value, parameterName);
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(bounded);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The value must be canonical Base64.", parameterName, exception);
        }

        try
        {
            if (decoded.Length == 0
                || !string.Equals(Convert.ToBase64String(decoded), bounded, StringComparison.Ordinal))
            {
                throw new ArgumentException("The value must be canonical Base64.", parameterName);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
        return bounded;
    }

    public static string RequireType7(string? value, string parameterName) =>
        RequireSinglePayloadEncString(value, "7.", expectedLength: null, parameterName);

    public static string RequireType4Rsa2048(string? value, string parameterName) =>
        RequireSinglePayloadEncString(value, "4.", expectedLength: 256, parameterName);

    public static string RequireLegacyType2(string? value, string parameterName)
    {
        var bounded = RequireWireValue(value, parameterName);
        if (!bounded.StartsWith("2.", StringComparison.Ordinal))
            throw new ArgumentException("The value must be a legacy type-2 EncString.", parameterName);
        var fields = bounded.AsSpan(2).ToString().Split('|');
        if (fields.Length != 3)
            throw new ArgumentException("The value must be a legacy type-2 EncString.", parameterName);

        var iv = DecodeCanonical(fields[0], parameterName);
        var ciphertext = DecodeCanonical(fields[1], parameterName);
        var mac = DecodeCanonical(fields[2], parameterName);
        try
        {
            if (iv.Length != 16
                || ciphertext.Length is 0
                || ciphertext.Length % 16 != 0
                || mac.Length != 32)
            {
                throw new ArgumentException("The legacy type-2 EncString has invalid fields.", parameterName);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mac);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(iv);
        }
        return bounded;
    }

    private static string RequireSinglePayloadEncString(
        string? value,
        string prefix,
        int? expectedLength,
        string parameterName)
    {
        var bounded = RequireWireValue(value, parameterName);
        if (!bounded.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException("The value has an unexpected EncString type.", parameterName);
        var decoded = DecodeCanonical(bounded[prefix.Length..], parameterName);
        try
        {
            if (decoded.Length == 0
                || expectedLength is int length && decoded.Length != length)
            {
                throw new ArgumentException("The EncString payload length is invalid.", parameterName);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
        return bounded;
    }

    private static string RequireWireValue(string? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is 0 or > TdeV2JitPending.MaximumWireValueLength
            || value.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException(
                "The wire value is empty, oversized, or contains control characters.",
                parameterName);
        }
        return value;
    }

    private static byte[] DecodeCanonical(string value, string parameterName)
    {
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The value must contain canonical Base64.", parameterName, exception);
        }
        if (!string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw new ArgumentException("The value must contain canonical Base64.", parameterName);
        }
        return decoded;
    }
}
