namespace App.ViewModels.Models;

public partial record CustomField(string Label, string Value, CipherEditorFieldType Type = CipherEditorFieldType.Text)
{
    public bool IsSecret => Type is
        CipherEditorFieldType.Hidden or
        CipherEditorFieldType.Linked or
        CipherEditorFieldType.Unsupported;
}

public sealed partial record PasswordHistoryItem(string Password, string DateLabel);

public sealed partial record LoginUriDetail(string Value, string Label);

public sealed partial record PasskeyDetail(
    string? RpId,
    string? UserName,
    string? UserDisplayName,
    bool Discoverable,
    DateTimeOffset? Created)
{
    public string? DisplayName => string.IsNullOrWhiteSpace(UserDisplayName) ? UserName : UserDisplayName;
    public string DiscoverableText => Discoverable ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB5141D3D19E9A048) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0C70665B6EB65F1A);
    public string? CreatedText => Created?.LocalDateTime.ToString("yyyy/M/d HH:mm:ss");
    public string DisplaySummary => string.IsNullOrWhiteSpace(DisplayName) ? RpId ?? string.Empty : $"{RpId} · {DisplayName}";
    public string MetadataSummary => string.Join(" · ", new[]
    {
        Discoverable ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text76D9BFE23ED3FE36) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text206E9C19B999EBB4),
        Created is { } date ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextEB06F8E57F39E0D7,date.LocalDateTime) : null,
    }.Where(value => value is not null));
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
    public bool HasCustomFields => CustomFields.Count > 0;
    public string CustomFieldsHeader => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextDB16BBD9F435915F,CustomFields.Count);
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
    public string AttachmentHeader => HasAttachments ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text993CAB6B3938BC78,AttachmentCount) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC7225DBF105EEED5);

    public string HistoryText =>
        global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextE1C7B861E22734E6,Edited.LocalDateTime,Created.LocalDateTime);
}

public sealed partial class LoginDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Login;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public DateTimeOffset? PasswordRevisionDate { get; init; }
    public string PasswordEmptyText => SensitiveUnlocked ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2F5F1D6FBFB061ED) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAA7973F091718FFF);
    public string? TotpSecret { get; init; }
    public string? Uri { get; init; }
    public IReadOnlyList<string> Uris { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DisplayUris => Uris.Count > 0
        ? Uris
        : string.IsNullOrWhiteSpace(Uri) ? Array.Empty<string>() : new[] { Uri };
    public IReadOnlyList<LoginUriDetail> UriRows => DisplayUris
        .Select((value, index) => new LoginUriDetail(value, index == 0 ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA8ECB4B6DABEBC09) : string.Empty))
        .ToArray();
    public IReadOnlyList<PasskeyDetail> Passkeys { get; init; } = Array.Empty<PasskeyDetail>();
    public bool HasPasskeys => Passkeys.Count > 0;
    public IReadOnlyList<PasswordHistoryItem> PasswordHistory { get; init; } = Array.Empty<PasswordHistoryItem>();
    public bool HasPasswordHistory => PasswordHistory.Count > 0;
    public int PasswordHistoryCount => PasswordHistory.Count;
    public bool HasSecondaryDetails => HasPasskeys || HasPasswordHistory;
    public string PasswordHistorySummary => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text7E875776788F755A,PasswordHistoryCount);
    public string LatestPasswordHistoryText => HasPasswordHistory
        ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text1CD6D591BD2676D1,PasswordHistory[0].DateLabel)
        : string.Empty;
}

public sealed partial class CardDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Card;
    public string? Cardholder { get; init; }
    public string? Number { get; init; }
    public string? Expiry { get; init; }
    public string? Brand { get; init; }
    public string? Cvv { get; init; }
}

public sealed partial class IdentityDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Identity;
    public string? FullName { get; init; }
    public string? Username { get; init; }
    public string? Company { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? IdNumber { get; init; }
    public string? PassportNumber { get; init; }
    public string? LicenseNumber { get; init; }
    public string? Address { get; init; }
}

public sealed partial class NoteDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Note;
    public string? Content { get; init; }
}

public sealed partial class SshDetail : CipherDetail
{
    public override VaultItemKind Kind => VaultItemKind.Ssh;
    public string? PublicKey { get; init; }
    public string? PrivateKey { get; init; }
    public string? Fingerprint { get; init; }
}

public sealed partial class BankAccountDetail : CipherDetail
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

public sealed partial class DriversLicenseDetail : CipherDetail
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

public sealed partial class PassportDetail : CipherDetail
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
