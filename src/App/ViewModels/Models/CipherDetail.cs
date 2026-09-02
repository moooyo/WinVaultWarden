namespace App.ViewModels.Models;

public record CustomField(string Label, string Value, CipherEditorFieldType Type = CipherEditorFieldType.Text)
{
    public bool IsSecret => Type is
        CipherEditorFieldType.Hidden or
        CipherEditorFieldType.Linked or
        CipherEditorFieldType.Unsupported;
}

public sealed record PasswordHistoryItem(string Password, string DateLabel);

public sealed record PasskeyDetail(
    string? RpId,
    string? UserName,
    string? UserDisplayName,
    bool Discoverable,
    DateTimeOffset? Created)
{
    public string? DisplayName => string.IsNullOrWhiteSpace(UserDisplayName) ? UserName : UserDisplayName;
    public string DiscoverableText => Discoverable ? "是" : "否";
    public string? CreatedText => Created?.LocalDateTime.ToString("yyyy/M/d HH:mm:ss");
}

// 详情基类。共用:名称、文件夹、备注、自定义字段、时间。
public abstract class CipherDetail
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? FolderName { get; init; }
    public abstract VaultItemKind Kind { get; }
    public string? Notes { get; init; }
    public IReadOnlyList<CustomField> CustomFields { get; init; } = Array.Empty<CustomField>();
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Edited { get; init; }
    public bool IsDeleted { get; init; }
    public bool IsArchived { get; init; }
    public bool CanEdit { get; init; } = true;
    public bool CanDelete { get; init; } = true;
    public bool CanRestore { get; init; } = true;
    public bool Favorite { get; init; }
    public bool Reprompt { get; init; }

    // 本投影是否已解锁敏感字段(密码/TOTP/隐藏字段/密码历史等)。
    // Reprompt==false 的条目恒为 true;Reprompt==true 的条目仅在条目级门禁验证通过后为 true。
    // 为 false 时上述敏感字段一律脱敏(null / 空集合 / 隐藏字段值置空),UI 显示"验证后显示敏感字段"入口。
    public bool SensitiveUnlocked { get; init; }
    public IReadOnlyList<AttachmentItem> Attachments { get; init; } = Array.Empty<AttachmentItem>();
    public int AttachmentCount => Attachments.Count;
    public bool HasAttachments => Attachments.Count > 0;
    public string AttachmentHeader => HasAttachments ? $"附件 ({AttachmentCount})" : "附件";

    public string HistoryText =>
        $"最后编辑:{Edited.LocalDateTime:yyyy/M/d HH:mm:ss}　创建于:{Created.LocalDateTime:yyyy/M/d HH:mm:ss}";
}

public sealed class LoginDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Login;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? TotpSecret { get; init; }
    public string? Uri { get; init; }
    public IReadOnlyList<PasskeyDetail> Passkeys { get; init; } = Array.Empty<PasskeyDetail>();
    public bool HasPasskeys => Passkeys.Count > 0;
    public IReadOnlyList<PasswordHistoryItem> PasswordHistory { get; init; } = Array.Empty<PasswordHistoryItem>();
    public bool HasPasswordHistory => PasswordHistory.Count > 0;
    public int PasswordHistoryCount => PasswordHistory.Count;
}

public sealed class CardDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Card;
    public string? Cardholder { get; init; }
    public string? Number { get; init; }
    public string? Expiry { get; init; }
    public string? Brand { get; init; }
    public string? Cvv { get; init; }
}

public sealed class IdentityDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Identity;
    public string? FullName { get; init; }
    public string? Username { get; init; }
    public string? Company { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? IdNumber { get; init; }
    public string? Address { get; init; }
}

public sealed class NoteDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Note;
    public string? Content { get; init; }
}

public sealed class SshDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Ssh;
    public string? PublicKey { get; init; }
    public string? PrivateKey { get; init; }
    public string? Fingerprint { get; init; }
}

public sealed class BankAccountDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.BankAccount;
    public string? BankName { get; init; }
    public string? NameOnAccount { get; init; }
    public string? AccountType { get; init; }
    public string? AccountNumber { get; init; }
    public string? RoutingNumber { get; init; }
    public string? BranchNumber { get; init; }
    public string? Pin { get; init; }
    public string? SwiftCode { get; init; }
    public string? Iban { get; init; }
    public string? BankContactPhone { get; init; }
}

public sealed class DriversLicenseDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.DriversLicense;
    public string? FullName { get; init; }
    public string? DateOfBirth { get; init; }
    public string? LicenseNumber { get; init; }
    public string? IssuingCountry { get; init; }
    public string? IssuingState { get; init; }
    public string? IssueDate { get; init; }
    public string? ExpirationDate { get; init; }
    public string? IssuingAuthority { get; init; }
    public string? LicenseClass { get; init; }
}

public sealed class PassportDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Passport;
    public string? FullName { get; init; }
    public string? DateOfBirth { get; init; }
    public string? Sex { get; init; }
    public string? BirthPlace { get; init; }
    public string? Nationality { get; init; }
    public string? IssuingCountry { get; init; }
    public string? PassportNumber { get; init; }
    public string? PassportType { get; init; }
    public string? NationalIdentificationNumber { get; init; }
    public string? IssuingAuthority { get; init; }
    public string? IssueDate { get; init; }
    public string? ExpirationDate { get; init; }
}
