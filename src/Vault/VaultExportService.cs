using Core.Services;

namespace Vault;

// 导出已解锁的密码库为 Bitwarden JSON 或 CSV 文本；回收站条目一律排除。
public sealed class VaultExportService(IVaultService vault) : IVaultExportService
{
    public bool CanExport => !VaultPolicyEvaluator.IsEnabledForUser(
        vault.Snapshot,
        Core.Enums.OrganizationPolicyType.DisablePersonalVaultExport);

    public string Export(ExportFormat format)
    {
        VaultPolicyEvaluator.ThrowIfEnabled(
            vault.Snapshot,
            Core.Enums.OrganizationPolicyType.DisablePersonalVaultExport,
            "An organization policy disables personal vault export.");
        var ciphers = vault.GetCiphers()
            .Where(cipher => !cipher.IsDeleted && string.IsNullOrWhiteSpace(cipher.OrganizationId))
            .ToList();
        var folders = vault.GetFolders();
        return format == ExportFormat.Json
            ? Porting.BitwardenJsonCodec.Serialize(ciphers, folders)
            : Porting.BitwardenCsvCodec.Serialize(ciphers, folders);
    }
}
