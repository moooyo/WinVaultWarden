using System.Security.Cryptography;
using Crypto;
using Crypto.V2;

namespace Vault;

/// <summary>
/// Owns exactly one account user-key representation. V1 keys are legacy AES-CBC-HMAC keys;
/// V2 keys are key-id-bound XAES-256-GCM COSE keys.
/// </summary>
public sealed class VaultUserKey : IDisposable
{
    private SymmetricCryptoKey? _legacy;
    private V2UserKey? _v2;
    private int _disposed;

    private VaultUserKey(SymmetricCryptoKey legacy) => _legacy = legacy;

    private VaultUserKey(V2UserKey v2) => _v2 = v2;

    public bool IsV2
    {
        get
        {
            ThrowIfDisposed();
            return _v2 is not null;
        }
    }

    public int SecurityVersion => IsV2 ? 2 : 1;

    internal string? KeyId
    {
        get
        {
            ThrowIfDisposed();
            return _v2?.KeyIdHex;
        }
    }

    internal SymmetricCryptoKey? Legacy
    {
        get
        {
            ThrowIfDisposed();
            return _legacy;
        }
    }

    internal V2UserKey? V2
    {
        get
        {
            ThrowIfDisposed();
            return _v2;
        }
    }

    public static VaultUserKey TakeOwnership(SymmetricCryptoKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new VaultUserKey(key);
    }

    public static VaultUserKey TakeOwnership(V2UserKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new VaultUserKey(key);
    }

    internal static VaultUserKey ImportOwnedTransport(
        byte[] encodedKey,
        int securityVersion)
    {
        ArgumentNullException.ThrowIfNull(encodedKey);

        switch (securityVersion)
        {
            case 1:
                if (encodedKey.Length != 64)
                {
                    CryptographicOperations.ZeroMemory(encodedKey);
                    throw new CryptographicException(
                        "A V1 user key must contain exactly 64 bytes.");
                }

                try
                {
                    return TakeOwnership(new SymmetricCryptoKey(encodedKey));
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(encodedKey);
                    throw;
                }

            case 2:
                try
                {
                    return TakeOwnership(V2UserKey.ImportTransport(encodedKey));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encodedKey);
                }

            default:
                CryptographicOperations.ZeroMemory(encodedKey);
                throw new CryptographicException(
                    $"Unsupported user-key security version: {securityVersion}.");
        }
    }

    public VaultUserKey Copy()
    {
        ThrowIfDisposed();
        if (_legacy is not null)
            return TakeOwnership(new SymmetricCryptoKey(_legacy.FullKey.ToArray()));

        var transport = _v2!.ExportTransport();
        try
        {
            return TakeOwnership(V2UserKey.ImportTransport(transport));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transport);
        }
    }

    internal bool SharesBackingKeyWith(VaultUserKey other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ThrowIfDisposed();
        other.ThrowIfDisposed();
        return _legacy is not null && ReferenceEquals(_legacy, other._legacy)
            || _v2 is not null && ReferenceEquals(_v2, other._v2);
    }

    internal void RelinquishBackingKeyOwnership()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _legacy = null;
        _v2 = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _legacy?.Dispose();
        _v2?.Dispose();
        _legacy = null;
        _v2 = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
