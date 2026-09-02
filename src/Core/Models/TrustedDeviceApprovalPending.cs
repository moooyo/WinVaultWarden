using System.Security.Cryptography;

namespace Core.Models;

public enum TrustedDeviceApprovalRequestType
{
    AuthenticateAndUnlock = 0,
    AdminApproval = 2,
}

/// <summary>
/// Sensitive client state required to poll and complete one trusted-device approval request.
/// </summary>
public sealed class TrustedDeviceApprovalPending : IDisposable
{
    public const int MaximumRequestIdLength = 256;
    public const int MaximumEndpointLength = 2048;
    public const int MaximumPrivateKeyLength = 4096;

    public static readonly TimeSpan AuthenticateAndUnlockLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan AdminApprovalLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(8);

    private readonly object _gate = new();
    private byte[]? _privateKeyPkcs8;

    public TrustedDeviceApprovalPending(
        string requestId,
        ReadOnlySpan<byte> privateKeyPkcs8,
        string accountId,
        ServerEndpoints endpoints,
        string deviceIdentifier,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        TrustedDeviceApprovalRequestType requestType)
    {
        RequestId = NormalizeRequestId(requestId);
        Binding = new DeviceKeyBinding(accountId, endpoints, NormalizeDeviceIdentifier(deviceIdentifier));
        ValidateEndpoints(endpoints);
        if (!Enum.IsDefined(requestType))
            throw new ArgumentOutOfRangeException(nameof(requestType));

        CreatedAt = createdAt.ToUniversalTime();
        ExpiresAt = expiresAt.ToUniversalTime();
        if (CreatedAt < DateTimeOffset.UnixEpoch
            || ExpiresAt <= CreatedAt
            || ExpiresAt - CreatedAt > MaximumLifetime)
        {
            throw new ArgumentException(
                "Approval timestamps must define a positive lifetime of no more than 8 days.",
                nameof(expiresAt));
        }

        byte[]? ownedPrivateKey = privateKeyPkcs8.ToArray();
        try
        {
            ValidatePrivateKey(ownedPrivateKey);
            _privateKeyPkcs8 = ownedPrivateKey;
            ownedPrivateKey = null;
        }
        finally
        {
            if (ownedPrivateKey is not null)
                CryptographicOperations.ZeroMemory(ownedPrivateKey);
        }
        RequestType = requestType;
    }

    public string RequestId { get; }
    public DeviceKeyBinding Binding { get; }
    public string AccountId => Binding.AccountId;
    public ServerEndpoints Endpoints => Binding.Endpoints;
    public string DeviceIdentifier => Binding.DeviceIdentifier;
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public TrustedDeviceApprovalRequestType RequestType { get; }

    public bool IsExpired(DateTimeOffset now) => now.ToUniversalTime() >= ExpiresAt;

    /// <summary>
    /// Returns a caller-owned private-key copy. The caller must zero it after use.
    /// </summary>
    public byte[] CopyPrivateKeyPkcs8()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_privateKeyPkcs8 is null, this);
            return _privateKeyPkcs8.ToArray();
        }
    }

    public TrustedDeviceApprovalPending Clone()
    {
        var privateKey = CopyPrivateKeyPkcs8();
        try
        {
            return new TrustedDeviceApprovalPending(
                RequestId,
                privateKey,
                AccountId,
                Endpoints,
                DeviceIdentifier,
                CreatedAt,
                ExpiresAt,
                RequestType);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    public void Dispose()
    {
        byte[]? privateKey;
        lock (_gate)
        {
            privateKey = _privateKeyPkcs8;
            _privateKeyPkcs8 = null;
        }
        if (privateKey is not null)
            CryptographicOperations.ZeroMemory(privateKey);
    }

    public static TrustedDeviceApprovalPending CreateWithDefaultLifetime(
        string requestId,
        ReadOnlySpan<byte> privateKeyPkcs8,
        string accountId,
        ServerEndpoints endpoints,
        string deviceIdentifier,
        DateTimeOffset createdAt,
        TrustedDeviceApprovalRequestType requestType)
    {
        var lifetime = requestType switch
        {
            TrustedDeviceApprovalRequestType.AuthenticateAndUnlock =>
                AuthenticateAndUnlockLifetime,
            TrustedDeviceApprovalRequestType.AdminApproval => AdminApprovalLifetime,
            _ => throw new ArgumentOutOfRangeException(nameof(requestType)),
        };
        return new TrustedDeviceApprovalPending(
            requestId,
            privateKeyPkcs8,
            accountId,
            endpoints,
            deviceIdentifier,
            createdAt,
            createdAt + lifetime,
            requestType);
    }

    private static void ValidatePrivateKey(ReadOnlySpan<byte> privateKeyPkcs8)
    {
        if (privateKeyPkcs8.Length is <= 0 or > MaximumPrivateKeyLength)
        {
            throw new ArgumentException(
                "The approval private key has an invalid length.",
                nameof(privateKeyPkcs8));
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(privateKeyPkcs8, out var bytesRead);
            if (bytesRead != privateKeyPkcs8.Length || rsa.KeySize != 2048)
            {
                throw new CryptographicException(
                    "The approval private key must be an exact RSA-2048 PKCS#8 value.");
            }
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException(
                "The approval private key must be an exact RSA-2048 PKCS#8 value.",
                nameof(privateKeyPkcs8),
                exception);
        }
    }

    private static string NormalizeRequestId(string? requestId)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        if (requestId.Length > MaximumRequestIdLength
            || !Guid.TryParseExact(requestId, "D", out var parsed))
        {
            throw new ArgumentException(
                "The approval request identifier must be a canonical GUID.",
                nameof(requestId));
        }
        return parsed.ToString("D");
    }

    private static string NormalizeDeviceIdentifier(string? deviceIdentifier)
    {
        ArgumentNullException.ThrowIfNull(deviceIdentifier);
        if (!Guid.TryParseExact(deviceIdentifier, "D", out var parsed))
        {
            throw new ArgumentException(
                "The device identifier must be a canonical GUID.",
                nameof(deviceIdentifier));
        }
        return parsed.ToString("D");
    }

    private static void ValidateEndpoints(ServerEndpoints endpoints)
    {
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
                nameof(endpoints));
        }
    }
}
