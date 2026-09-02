using Core.Models;

namespace Core.Session;

public interface IVaultSnapshot
{
    VaultState State { get; }
    IReadOnlyList<Cipher> Ciphers { get; }
    IReadOnlyList<Folder> Folders { get; }
    IReadOnlyList<DeviceInfo> Devices { get; }
    IReadOnlyList<Send> Sends => Array.Empty<Send>();
    IReadOnlyList<VaultOrganization> Organizations => Array.Empty<VaultOrganization>();
    IReadOnlyList<VaultCollection> Collections => Array.Empty<VaultCollection>();
    IReadOnlyList<VaultOrganizationPolicy> Policies => Array.Empty<VaultOrganizationPolicy>();
    IReadOnlyList<VaultOrganizationPolicyContext> PolicyOrganizations =>
        Array.Empty<VaultOrganizationPolicyContext>();
    VaultDomainSettings? Domains => null;
    AccountInfo Account { get; }
    int SkippedCipherCount { get; }
    int SkippedFolderCount { get; }
    int SkippedSendCount => 0;
    int UnsupportedOrganizationCipherCount => 0;
    int UnsupportedProviderOrganizationCount => 0;
    VaultCapabilities Capabilities => VaultCapabilities.None;
    bool DataStale { get; }
    long Generation { get; }
}
