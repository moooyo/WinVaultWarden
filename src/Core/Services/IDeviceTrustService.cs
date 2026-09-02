using Core.Models;
using Core.Session;

namespace Core.Services;

public interface IDeviceTrustService
{
    bool CanTrustCurrentDevice { get; }

    bool IsCurrentDeviceTrusted { get; }

    void SetCurrentSessionCapability(VaultSessionLease lease, bool supported);

    /// <summary>
    /// Records that the server no longer trusts one exact local device binding.
    /// Implementations should prevent any retained key from being reused until
    /// a subsequent enrollment safely replaces it.
    /// </summary>
    void RecordTrustLoss(
        string accountId,
        ServerEndpoints endpoints,
        string deviceIdentifier)
    {
    }

    /// <summary>
    /// Decrypts a user key for one exact device binding. On success, the caller
    /// owns the returned buffer and must transfer or zero it. Failure returns an
    /// empty buffer or throws <see cref="DeviceTrustException"/>.
    /// </summary>
    bool TryDecryptUserKey(
        string accountId,
        ServerEndpoints endpoints,
        string deviceIdentifier,
        string encryptedPrivateKey,
        string encryptedUserKey,
        out byte[] userKey);

    Task TrustCurrentDeviceAsync(CancellationToken ct = default);
}

public sealed class DeviceTrustException : Exception
{
    public DeviceTrustException(string message)
        : base(message)
    {
    }

    public DeviceTrustException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
