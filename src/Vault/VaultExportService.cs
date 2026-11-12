using Core.Services;

namespace Vault;

// 导出已解锁的密码库为 Bitwarden JSON 或 CSV 文本；回收站条目一律排除。
public sealed class VaultExportService(IVaultService vault, VaultSession? session = null) : IVaultExportService
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
        return format switch
        {
            ExportFormat.Json => Porting.BitwardenJsonCodec.Serialize(ciphers, folders),
            ExportFormat.Csv => Porting.BitwardenCsvCodec.Serialize(ciphers, folders),
            _ => throw new ArgumentException("Use the asynchronous protected export operation for an encrypted backup.", nameof(format)),
        };
    }

    public async Task<string> ExportProtectedAsync(
        ExportFormat format,
        string? password,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (format is ExportFormat.Json or ExportFormat.Csv)
            return Export(format);
        if (format is not (ExportFormat.PasswordProtectedJson or ExportFormat.AccountRestrictedJson))
            throw new ArgumentOutOfRangeException(nameof(format));
        if (format == ExportFormat.PasswordProtectedJson
            && (password is null || password.Length < VaultPortingLimits.MinimumExportPasswordLength || password.Length > 1024))
        {
            throw new ArgumentException("The backup password must contain between 12 and 1024 characters.", nameof(password));
        }
        if (session is null)
            throw new InvalidOperationException("Protected export requires an account-bound vault session.");

        var lease = session.CaptureLease();
        if (!session.IsUnlocked(lease))
            throw new InvalidOperationException("The vault must be unlocked before exporting a backup.");
        Core.Models.Cipher[] ciphers = [];
        Core.Models.Folder[] folders = [];
        Core.Models.VaultCapabilities capabilities = Core.Models.VaultCapabilities.None;
        if (!session.TryRunIfUnlocked(lease, () =>
        {
            VaultPolicyEvaluator.ThrowIfEnabled(
                vault.Snapshot,
                Core.Enums.OrganizationPolicyType.DisablePersonalVaultExport,
                "An organization policy disables personal vault export.");
            ciphers = vault.GetCiphers(lease)
                .Where(cipher => !cipher.IsDeleted && string.IsNullOrWhiteSpace(cipher.OrganizationId))
                .ToArray();
            folders = vault.GetFolders().ToArray();
            capabilities = session.GetCapabilities(lease);
        }))
            throw new OperationCanceledException(lease.CancellationToken);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        string exported;
        if (format == ExportFormat.PasswordProtectedJson)
        {
            exported = await Task.Run(() =>
            {
                var plaintext = Porting.BitwardenJsonCodec.Serialize(ciphers, folders);
                VaultPortingLimits.ValidateContent(plaintext);
                return Porting.EncryptedBitwardenJsonCodec.EncryptWithPassword(plaintext, password!);
            }, linked.Token).ConfigureAwait(false);
        }
        else
        {
            if (!session.TryCopyUserKeyMaterial(lease, out var key) || key is null)
                throw new InvalidOperationException("The current account key is unavailable.");
            using (key)
            {
                exported = await Task.Run(
                    () => Porting.EncryptedBitwardenJsonCodec.ExportAccountRestricted(ciphers, folders, key, capabilities),
                    linked.Token).ConfigureAwait(false);
            }
        }

        linked.Token.ThrowIfCancellationRequested();
        session.ThrowIfExpired(lease);
        VaultPortingLimits.ValidateContent(exported);
        return exported;
    }
}
