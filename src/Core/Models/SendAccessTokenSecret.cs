using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Core.Models;

/// <summary>
/// Stores a scoped Send access token in mutable memory so the retained copy can be cleared.
/// The immutable string required by HttpClient is created only for the duration of one request.
/// </summary>
internal sealed class SendAccessTokenSecret : IDisposable
{
    private readonly object _gate = new();
    private char[]? _value;

    internal SendAccessTokenSecret(
        string value,
        DateTimeOffset expiresAt)
        : this(CopyValue(value), expiresAt)
    {
    }

    private SendAccessTokenSecret(
        char[] value,
        DateTimeOffset expiresAt)
    {
        _value = value;
        ExpiresAt = expiresAt;
    }

    internal DateTimeOffset ExpiresAt { get; }

    internal SendAccessTokenSecret Clone()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_value is null, this);
            return new SendAccessTokenSecret(_value.ToArray(), ExpiresAt);
        }
    }

    internal SendAccessTokenSnapshot? TryAcquireSnapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_value is null, this);
            return now < ExpiresAt
                ? new SendAccessTokenSnapshot(_value)
                : null;
        }
    }

    public void Dispose()
    {
        char[]? value;
        lock (_gate)
        {
            value = _value;
            _value = null;
        }

        Clear(value);
    }

    private static char[] CopyValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Send access token is required.", nameof(value));

        return value.ToCharArray();
    }

    internal static void Clear(char[]? value)
    {
        if (value is not null)
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(value.AsSpan()));
    }
}

/// <summary>
/// Owns a short-lived copy of a Send access token for one HTTP request.
/// </summary>
internal sealed class SendAccessTokenSnapshot : IDisposable
{
    private readonly object _gate = new();
    private char[]? _value;

    internal SendAccessTokenSnapshot(char[] value) => _value = value.ToArray();

    internal string Reveal()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_value is null, this);
            return new string(_value);
        }
    }

    public void Dispose()
    {
        char[]? value;
        lock (_gate)
        {
            value = _value;
            _value = null;
        }

        SendAccessTokenSecret.Clear(value);
    }
}
