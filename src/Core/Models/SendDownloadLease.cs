using System.Security.Cryptography;

namespace Core.Models;

/// <summary>
/// Owns the immutable routing data and a private Send seed copy for one file download. The lease is
/// independent from the UI result that created it, so replacing or clearing that result cannot race
/// an in-progress save operation.
/// </summary>
public sealed class SendDownloadLease : IDisposable
{
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _accessTokenRefreshGate = new(1, 1);
    private byte[]? _seed;
    private SendAccessTokenSecret? _accessToken;

    internal SendDownloadLease(
        ServerEndpoints endpoints,
        string accessId,
        string? sendId,
        string? fileId,
        string? fileDownloadUrl,
        string? passwordProof,
        bool usesAccessToken,
        byte[] seed,
        SendAccessTokenSecret? accessToken)
    {
        Endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        AccessId = string.IsNullOrWhiteSpace(accessId)
            ? throw new ArgumentException("Send access id is required.", nameof(accessId))
            : accessId;
        SendId = sendId;
        FileId = fileId;
        FileDownloadUrl = fileDownloadUrl;
        PasswordProof = passwordProof;
        UsesAccessToken = usesAccessToken;
        _seed = seed.ToArray();
        _accessToken = accessToken;
    }

    public ServerEndpoints Endpoints { get; }
    public string AccessId { get; }
    public string? SendId { get; }
    public string? FileId { get; }
    public string? FileDownloadUrl { get; }
    public string? PasswordProof { get; }
    public bool UsesAccessToken { get; }

    public byte[] CopySeed()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_seed is null, this);
            return _seed.ToArray();
        }
    }

    internal SendAccessTokenSnapshot? TryAcquireAccessTokenSnapshot(DateTimeOffset now)
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_seed is null, this);
            return _accessToken?.TryAcquireSnapshot(now);
        }
    }

    internal async ValueTask<IDisposable> AcquireAccessTokenRefreshLockAsync(
        CancellationToken cancellationToken)
    {
        await _accessTokenRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            if (_seed is null)
            {
                _accessTokenRefreshGate.Release();
                throw new ObjectDisposedException(nameof(SendDownloadLease));
            }
        }

        return new RefreshLock(_accessTokenRefreshGate);
    }

    internal void StoreAccessToken(
        string value,
        DateTimeOffset expiresAt)
    {
        var replacement = new SendAccessTokenSecret(value, expiresAt);
        SendAccessTokenSecret? previous;
        lock (_stateGate)
        {
            if (_seed is null)
            {
                replacement.Dispose();
                throw new ObjectDisposedException(nameof(SendDownloadLease));
            }

            previous = _accessToken;
            _accessToken = replacement;
        }

        previous?.Dispose();
    }

    public void Dispose()
    {
        byte[]? seed;
        SendAccessTokenSecret? accessToken;
        lock (_stateGate)
        {
            seed = _seed;
            _seed = null;
            accessToken = _accessToken;
            _accessToken = null;
        }

        if (seed is not null)
            CryptographicOperations.ZeroMemory(seed);
        accessToken?.Dispose();
    }

    private sealed class RefreshLock(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
