using System.Security.Cryptography;
using Core.Models;

namespace Crypto;

/// <summary>
/// Owns the bounded routing data and decoded seed from one validated Send share URL.
/// </summary>
public sealed class ParsedSendShareUrl : IDisposable
{
    private readonly object _seedGate = new();
    private byte[]? _seed;

    internal ParsedSendShareUrl(
        Uri shareUri,
        string accessId,
        ServerEndpoints endpoints,
        byte[] seed)
    {
        ShareUri = shareUri ?? throw new ArgumentNullException(nameof(shareUri));
        AccessId = accessId ?? throw new ArgumentNullException(nameof(accessId));
        Endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _seed = seed ?? throw new ArgumentNullException(nameof(seed));
    }

    public Uri ShareUri { get; }
    public string AccessId { get; }
    public ServerEndpoints Endpoints { get; }

    public byte[] CopySeed()
    {
        lock (_seedGate)
        {
            ObjectDisposedException.ThrowIf(_seed is null, this);
            return _seed.ToArray();
        }
    }

    public void Dispose()
    {
        byte[]? seed;
        lock (_seedGate)
        {
            seed = _seed;
            _seed = null;
        }

        if (seed is not null)
            CryptographicOperations.ZeroMemory(seed);
    }
}
