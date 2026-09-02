using System.Security.Cryptography;

namespace Core.Models;

public sealed record LoginWithDeviceBeginResult(
    string RequestId,
    string FingerprintPhrase,
    DateTimeOffset ExpiresAtUtc);

public enum LoginWithDevicePollStatus
{
    Pending,
    Denied,
    Expired,
    Approved,
}

public sealed class LoginWithDevicePollResult
{
    public LoginWithDevicePollResult(
        LoginWithDevicePollStatus status,
        LoginWithDeviceGrant? grant = null)
    {
        if ((status == LoginWithDevicePollStatus.Approved) != (grant is not null))
        {
            throw new ArgumentException(
                "Only an approved device-login result may contain a grant.",
                nameof(grant));
        }

        Status = status;
        Grant = grant;
    }

    public LoginWithDevicePollStatus Status { get; }

    public LoginWithDeviceGrant? Grant { get; }
}

/// <summary>
/// Owns the one-time access code and decrypted user-key payload for one approved
/// device-login request. Callers must dispose the grant after the token exchange.
/// </summary>
public sealed class LoginWithDeviceGrant : IDisposable
{
    private char[]? _accessCode;
    private byte[]? _userKey;
    private int _disposed;

    public LoginWithDeviceGrant(
        string requestId,
        string accessCode,
        string email,
        ServerEndpoints endpoints,
        string deviceIdentifier,
        byte[] userKey,
        int userKeySecurityVersion)
        : this(
            requestId,
            accessCode?.ToCharArray()
                ?? throw new ArgumentNullException(nameof(accessCode)),
            email,
            endpoints,
            deviceIdentifier,
            userKey,
            userKeySecurityVersion)
    {
    }

    internal LoginWithDeviceGrant(
        string requestId,
        char[] accessCode,
        string email,
        ServerEndpoints endpoints,
        string deviceIdentifier,
        byte[] userKey,
        int userKeySecurityVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(accessCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentifier);
        ArgumentNullException.ThrowIfNull(userKey);
        if (accessCode.Length != 25)
            throw new ArgumentException("The access code must contain exactly 25 characters.", nameof(accessCode));
        if (userKeySecurityVersion is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(userKeySecurityVersion));
        if (userKeySecurityVersion == 1 && userKey.Length != 64)
            throw new ArgumentException("A V1 user key must contain exactly 64 bytes.", nameof(userKey));

        RequestId = requestId;
        Email = email;
        Endpoints = endpoints;
        DeviceIdentifier = deviceIdentifier;
        UserKeySecurityVersion = userKeySecurityVersion;
        _accessCode = accessCode;
        _userKey = userKey;
    }

    internal string RequestId { get; }

    internal string Email { get; }

    internal ServerEndpoints Endpoints { get; }

    internal string DeviceIdentifier { get; }

    internal int UserKeySecurityVersion { get; }

    internal ReadOnlyMemory<char> AccessCode
    {
        get
        {
            ThrowIfDisposed();
            return _accessCode!;
        }
    }

    internal ReadOnlySpan<byte> UserKey
    {
        get
        {
            ThrowIfDisposed();
            return _userKey!;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_accessCode is not null)
            Array.Clear(_accessCode);
        if (_userKey is not null)
            CryptographicOperations.ZeroMemory(_userKey);
        _accessCode = null;
        _userKey = null;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
