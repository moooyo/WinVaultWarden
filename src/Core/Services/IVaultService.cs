using Core.Models;
using Core.Session;

namespace Core.Services;

public interface IVaultService
{
    IVaultSnapshot Snapshot { get; }
    IReadOnlyList<Cipher> GetCiphers();
    IReadOnlyList<Folder> GetFolders();
    IReadOnlyList<DeviceInfo> GetDevices();
    VaultSessionLease CaptureLease();
    bool IsCurrent(VaultSessionLease lease);
    void ThrowIfExpired(VaultSessionLease lease);
    IReadOnlyList<Cipher> GetCiphers(VaultSessionLease lease);
}
