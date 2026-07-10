using Core.Models;

namespace Core.Session;

public interface IVaultSnapshot
{
    VaultState State { get; }
    IReadOnlyList<Cipher> Ciphers { get; }
    IReadOnlyList<Folder> Folders { get; }
    IReadOnlyList<DeviceInfo> Devices { get; }
    AccountInfo Account { get; }
    int SkippedCipherCount { get; }
    bool DataStale { get; }
    long Generation { get; }
    int SkippedFolderCount { get; }
}
