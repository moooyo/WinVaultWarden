using System.Security.Cryptography;
using Core.Enums;

namespace Core.Models;

// Decrypted projection of an accessed Send. Seed is retained so a file download can
// derive the same crypto key. SendId and FileId identify the access-file request.
public sealed class SendAccessResult : IDisposable
{
    private ServerEndpoints? _endpoints;
    private readonly object _stateGate = new();
    private byte[]? _seed;
    private SendAccessTokenSecret? _accessToken;

    public SendType Type { get; init; }
    public string Name { get; init; } = "";
    public string? Notes { get; init; }
    public string? TextContent { get; init; }
    public bool TextHidden { get; init; }
    public string? CreatorIdentifier { get; init; }
    public DateTimeOffset? ExpirationDate { get; init; }
    public string? FileName { get; init; }
    public string? FileDownloadUrl { get; init; }
    /// <summary>The file Send UUID used by the access-file request.</summary>
    public string? SendId { get; init; }
    /// <summary>The file identifier used by the access-file request.</summary>
    public string? FileId { get; init; }
    public string AccessId { get; init; } = "";

    /// <summary>
    /// Gets the service endpoints derived from the share URL rather than from the
    /// currently signed-in account.
    /// </summary>
    public ServerEndpoints Endpoints
    {
        get => _endpoints
            ?? throw new InvalidOperationException("The Send access endpoints are not initialized.");
        init => _endpoints = value
            ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Compatibility view of the Web Vault origin. Setting it derives a self-hosted
    /// endpoint set; new code should set <see cref="Endpoints"/> instead.
    /// </summary>
    public ServerOrigin Origin
    {
        get => _endpoints?.WebVault.Origin ?? default;
        init => _endpoints = ServerEndpoints.FromWebVault(value.Value);
    }

    /// <summary>
    /// Returns an owned copy of the Send seed. Callers handling the copy must clear it when done.
    /// </summary>
    public byte[] Seed
    {
        get => CopySeed();
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _seed = value.ToArray();
        }
    }

    public byte[] CopySeed()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_seed is null, this);
            return _seed.ToArray();
        }
    }

    public SendDownloadLease AcquireDownloadLease()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_seed is null, this);
            var accessToken = _accessToken?.Clone();
            try
            {
                return new SendDownloadLease(
                    Endpoints,
                    AccessId,
                    SendId,
                    FileId,
                    FileDownloadUrl,
                    PasswordProof,
                    UsesAccessToken,
                    _seed,
                    accessToken);
            }
            catch
            {
                accessToken?.Dispose();
                throw;
            }
        }
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
                throw new ObjectDisposedException(nameof(SendAccessResult));
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

    /// <summary>The Base64 password proof resubmitted by the access-file request.</summary>
    public string? PasswordProof { get; init; }
    public bool UsesAccessToken { get; init; }
}
