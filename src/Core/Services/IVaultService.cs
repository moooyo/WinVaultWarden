using Core.Models;
using Core.Session;

namespace Core.Services;

public interface IVaultService
{
    IVaultSnapshot Snapshot { get; }
    IReadOnlyList<Cipher> GetCiphers();
    IReadOnlyList<Folder> GetFolders();
    IReadOnlyList<DeviceInfo> GetDevices();
}
