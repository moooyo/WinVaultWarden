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

    public IReadOnlyList<VaultOrganization> GetOrganizations() => _session.Organizations;

    public IReadOnlyList<VaultCollection> GetCollections() => _session.Collections;

    public IReadOnlyList<VaultOrganizationPolicy> GetPolicies() => _session.Policies;

    public VaultSessionLease CaptureLease() => _session.CaptureLease();

    public bool IsCurrent(VaultSessionLease lease) => _session.IsCurrent(lease);

    public void ThrowIfExpired(VaultSessionLease lease) => _session.ThrowIfExpired(lease);

    public IReadOnlyList<Cipher> GetCiphers(VaultSessionLease lease) =>
        _session.GetCiphers(lease);
}
