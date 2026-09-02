using Core.Models;

namespace Core.Abstractions;

public enum DeviceKeyRemovalStatus
{
    Removed,
    Absent,
    Unavailable,
}

/// <summary>
/// Persists a trusted-device encryption key for one exact account, server
/// environment, and device identifier.
/// </summary>
public interface IDeviceKeyStore
{
    public const int KeyLength = 64;

    /// <summary>
    /// Loads a device key. The caller owns the returned buffer and must clear
    /// it with <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory"/>.
    /// </summary>
    bool TryLoad(DeviceKeyBinding binding, out byte[] deviceKey);

    void Save(DeviceKeyBinding binding, ReadOnlySpan<byte> deviceKey);

    bool Remove(DeviceKeyBinding binding);

    /// <summary>
    /// Removes one exact binding while distinguishing absence from an unavailable deletion.
    /// The default is deliberately fail-closed because the legacy boolean contract does not
    /// distinguish a missing key from storage or protection failures.
    /// </summary>
    DeviceKeyRemovalStatus RemoveExact(DeviceKeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        byte[]? deviceKey = null;
        try
        {
            if (!TryLoad(binding, out deviceKey))
                return DeviceKeyRemovalStatus.Unavailable;
            return Remove(binding)
                ? DeviceKeyRemovalStatus.Removed
                : DeviceKeyRemovalStatus.Unavailable;
        }
        finally
        {
            if (deviceKey is not null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(deviceKey);
        }
    }

    /// <summary>
    /// Removes every device binding for one account and complete server environment.
    /// Implementations that cannot enumerate a canonical binding ledger fail closed.
    /// </summary>
    AccountScopedRemovalStatus RemoveAccountScope(
        string accountId,
        ServerEndpoints endpoints)
    {
        AccountScopedRemovalValidation.Validate(accountId, endpoints);
        return AccountScopedRemovalStatus.Unavailable;
    }
}
