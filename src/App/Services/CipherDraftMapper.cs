using App.ViewModels.Models;
using Core.Enums;
using Core.Models;

namespace App.Services;

// 编辑器草稿(逐字段) ⇄ 解密后的领域 Cipher 双向映射。
// 写入:ToCipher(draft, original) → CipherEncryptor 加密 → 写 API。编辑载入:ToDraft(cipher)。
// 编辑时(original 非空)保留 Id / 创建&修改时间 / OrganizationId / passkey(编辑器无 passkey UI)。
public static class CipherDraftMapper
{
    public static Cipher ToCipher(CipherEditorDraft draft, Cipher? original)
    {
        var type = TypeFor(draft.Type);
        var fields = draft.CustomFields
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .Select(ToField)
            .ToArray();

        return new Cipher
        {
            Id = original?.Id ?? string.Empty,
            Type = type,
            OrganizationId = original?.OrganizationId,
            FolderId = NullIfBlank(draft.FolderId),
            Favorite = draft.Favorite,
            Reprompt = draft.Reprompt,
            Name = draft.Name.Trim(),
            Notes = NullIfBlank(draft.Notes),
            CreationDate = original?.CreationDate ?? default,
            RevisionDate = original?.RevisionDate ?? default,
            DeletedDate = original?.DeletedDate,
            Login = type == CipherType.Login ? ToLogin(draft.Login, original?.Login) : null,
            Card = type == CipherType.Card ? ToCard(draft.Card) : null,
            Identity = type == CipherType.Identity ? ToIdentity(draft.Identity) : null,
            SecureNote = type == CipherType.SecureNote ? new CipherSecureNote(draft.SecureNote.Type) : null,
            Ssh = type == CipherType.SshKey ? ToSsh(draft.SshKey) : null,
            Fields = fields,
            PasswordHistory = BuildPasswordHistory(draft, original, DateTimeOffset.UtcNow),
        };
    }

    // 密码历史:编辑 Login 且密码发生变化时,把旧密码(带 now 时间戳)前插到历史最前,封顶 5 条。
    public static IReadOnlyList<PasswordHistoryEntry> BuildPasswordHistory(
        CipherEditorDraft draft, Cipher? original, DateTimeOffset now)
    {
        var baseline = original?.PasswordHistory ?? Array.Empty<PasswordHistoryEntry>();
        if (draft.Type != VaultItemKind.Login || original?.Login is null)
            return baseline;

        var oldPassword = original.Login.Password;
        var newPassword = draft.Login.Password;
        if (string.IsNullOrEmpty(oldPassword) || string.Equals(oldPassword, newPassword, StringComparison.Ordinal))
            return baseline;

        var result = new List<PasswordHistoryEntry>(baseline.Count + 1)
        {
            new(oldPassword, now),
        };
        result.AddRange(baseline);
        return result.Count > 5 ? result.GetRange(0, 5) : result;
    }

    public static CipherEditorDraft ToDraft(Cipher cipher)
    {
        var draft = new CipherEditorDraft
        {
            Type = KindFor(cipher.Type),
            Name = cipher.Name,
            FolderId = cipher.FolderId,
            Favorite = cipher.Favorite,
            Reprompt = cipher.Reprompt,
            Notes = cipher.Notes ?? string.Empty,
        };

        switch (cipher.Type)
        {
            case CipherType.Login when cipher.Login is { } login:
                draft.Login.Username = login.Username ?? string.Empty;
                draft.Login.Password = login.Password ?? string.Empty;
                draft.Login.Totp = login.Totp ?? string.Empty;
                draft.Login.Uris.Clear();
                if (login.Uris.Count == 0)
                    draft.Login.Uris.Add(new LoginUriEditorDraft());
                else
                    foreach (var uri in login.Uris)
                        draft.Login.Uris.Add(new LoginUriEditorDraft { Uri = uri.Uri ?? string.Empty, Match = uri.Match });
                break;
            case CipherType.Card when cipher.Card is { } card:
                draft.Card.CardholderName = card.CardholderName ?? string.Empty;
                draft.Card.Number = card.Number ?? string.Empty;
                draft.Card.ExpMonth = card.ExpMonth ?? string.Empty;
                draft.Card.ExpYear = card.ExpYear ?? string.Empty;
                draft.Card.Code = card.Code ?? string.Empty;
                draft.Card.Brand = card.Brand ?? string.Empty;
                break;
            case CipherType.Identity when cipher.Identity is { } id:
                draft.Identity.Title = id.Title ?? string.Empty;
                draft.Identity.FirstName = id.FirstName ?? string.Empty;
                draft.Identity.MiddleName = id.MiddleName ?? string.Empty;
                draft.Identity.LastName = id.LastName ?? string.Empty;
                draft.Identity.Username = id.Username ?? string.Empty;
                draft.Identity.Company = id.Company ?? string.Empty;
                draft.Identity.Ssn = id.Ssn ?? string.Empty;
                draft.Identity.PassportNumber = id.PassportNumber ?? string.Empty;
                draft.Identity.LicenseNumber = id.LicenseNumber ?? string.Empty;
                draft.Identity.Email = id.Email ?? string.Empty;
                draft.Identity.Phone = id.Phone ?? string.Empty;
                draft.Identity.Address1 = id.Address1 ?? string.Empty;
                draft.Identity.Address2 = id.Address2 ?? string.Empty;
                draft.Identity.Address3 = id.Address3 ?? string.Empty;
                draft.Identity.City = id.City ?? string.Empty;
                draft.Identity.State = id.State ?? string.Empty;
                draft.Identity.PostalCode = id.PostalCode ?? string.Empty;
                draft.Identity.Country = id.Country ?? string.Empty;
                break;
            case CipherType.SshKey when cipher.Ssh is { } ssh:
                draft.SshKey.PrivateKey = ssh.PrivateKey ?? string.Empty;
                draft.SshKey.PublicKey = ssh.PublicKey ?? string.Empty;
                draft.SshKey.KeyFingerprint = ssh.Fingerprint ?? string.Empty;
                break;
            case CipherType.SecureNote when cipher.SecureNote is { } note:
                draft.SecureNote.Type = note.Type;
                break;
        }

        foreach (var field in cipher.Fields)
            draft.CustomFields.Add(ToFieldDraft(field));

        return draft;
    }

    private static CipherLogin ToLogin(LoginEditorDraft login, CipherLogin? original)
    {
        var uris = login.Uris
            .Where(u => !string.IsNullOrWhiteSpace(u.Uri))
            .Select(u => new CipherLoginUri(u.Uri.Trim(), u.Match))
            .ToArray();

        return new CipherLogin(
            NullIfBlank(login.Username),
            NullIfBlank(login.Password),
            NullIfBlank(login.Totp),
            uris)
        {
            Fido2Credentials = original?.Fido2Credentials ?? Array.Empty<CipherFido2Credential>(),
        };
    }

    private static CipherCard ToCard(CardEditorDraft card) => new(
        NullIfBlank(card.CardholderName),
        NullIfBlank(card.Number),
        NullIfBlank(card.ExpMonth),
        NullIfBlank(card.ExpYear),
        NullIfBlank(card.Code),
        NullIfBlank(card.Brand));

    private static CipherIdentity ToIdentity(IdentityEditorDraft id) => new(
        NullIfBlank(id.Title),
        NullIfBlank(id.FirstName),
        NullIfBlank(id.MiddleName),
        NullIfBlank(id.LastName),
        NullIfBlank(id.Username),
        NullIfBlank(id.Company),
        NullIfBlank(id.Ssn),
        NullIfBlank(id.PassportNumber),
        NullIfBlank(id.LicenseNumber),
        NullIfBlank(id.Email),
        NullIfBlank(id.Phone),
        NullIfBlank(id.Address1),
        NullIfBlank(id.Address2),
        NullIfBlank(id.Address3),
        NullIfBlank(id.City),
        NullIfBlank(id.State),
        NullIfBlank(id.PostalCode),
        NullIfBlank(id.Country));

    private static CipherSsh ToSsh(SshKeyEditorDraft ssh) => new(
        NullIfBlank(ssh.PrivateKey),
        NullIfBlank(ssh.PublicKey),
        NullIfBlank(ssh.KeyFingerprint));

    private static CipherField ToField(CustomFieldEditorDraft field)
    {
        var type = field.Type switch
        {
            CipherEditorFieldType.Hidden => CipherFieldType.Hidden,
            CipherEditorFieldType.Boolean => CipherFieldType.Boolean,
            _ => CipherFieldType.Text,
        };
        var value = field.Type == CipherEditorFieldType.Boolean
            ? (field.BooleanValue ? "true" : "false")
            : NullIfBlank(field.Value);
        return new CipherField(field.Name.Trim(), value, type);
    }

    private static CustomFieldEditorDraft ToFieldDraft(CipherField field)
    {
        var type = field.Type switch
        {
            CipherFieldType.Hidden => CipherEditorFieldType.Hidden,
            CipherFieldType.Boolean => CipherEditorFieldType.Boolean,
            _ => CipherEditorFieldType.Text,
        };
        return new CustomFieldEditorDraft
        {
            Name = field.Name,
            Type = type,
            Value = type == CipherEditorFieldType.Boolean ? string.Empty : (field.Value ?? string.Empty),
            BooleanValue = type == CipherEditorFieldType.Boolean
                && string.Equals(field.Value, "true", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static CipherType TypeFor(VaultItemKind kind) => kind switch
    {
        VaultItemKind.Login => CipherType.Login,
        VaultItemKind.Card => CipherType.Card,
        VaultItemKind.Identity => CipherType.Identity,
        VaultItemKind.Note => CipherType.SecureNote,
        VaultItemKind.Ssh => CipherType.SshKey,
        _ => CipherType.Login,
    };

    private static VaultItemKind KindFor(CipherType type) => type switch
    {
        CipherType.Login => VaultItemKind.Login,
        CipherType.Card => VaultItemKind.Card,
        CipherType.Identity => VaultItemKind.Identity,
        CipherType.SecureNote => VaultItemKind.Note,
        CipherType.SshKey => VaultItemKind.Ssh,
        _ => VaultItemKind.Login,
    };

    // 字段值原样保留:机密(密码/TOTP/SSH 私钥)与备注可能含有意空白,trim 会静默损坏数据。
    // 仅空/空白 → null。项目 Name、URI、自定义字段名在各自调用点单独 trim(非机密标签)。
    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
