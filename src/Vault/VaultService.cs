using Core.Models;
using Core.Services;
using Core.Session;

namespace Vault;

public sealed class VaultService : IVaultService
{
    private readonly VaultSession _session;

    public VaultService(VaultSession session) => _session = session;

    public IVaultSnapshot Snapshot => _session;

    public IReadOnlyList<Cipher> GetCiphers() => _session.Ciphers;

    public IReadOnlyList<Folder> GetFolders() => _session.Folders;

    public IReadOnlyList<DeviceInfo> GetDevices() => _session.Devices;
}
