using Core.Models;

namespace Core.Session;

public interface IVaultSnapshot
{
    VaultState State { get; }
    IReadOnlyList<Cipher> Ciphers { get; }
    IReadOnlyList<Folder> Folders { get; }
    IReadOnlyList<DeviceInfo> Devices { get; }
    AccountInfo Account { get; }
}
