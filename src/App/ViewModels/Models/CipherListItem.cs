namespace App.ViewModels.Models;

public enum VaultItemKind { Login, Card, Identity, Note, Ssh, BankAccount, DriversLicense, Passport }

// 栏③ 列表项。Subtitle 随类型不同(登录=用户名,网站=域名等)。
public sealed partial class CipherListItem : System.ComponentModel.INotifyPropertyChanged
{
    private string _groupHeader = string.Empty;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string GroupHeader
    {
        get => _groupHeader;
        set
        {
            if (string.Equals(_groupHeader, value, StringComparison.Ordinal))
                return;
            _groupHeader = value;
            PropertyChanged?.Invoke(this, new(nameof(GroupHeader)));
            PropertyChanged?.Invoke(this, new(nameof(HasGroupHeader)));
        }
    }
    public bool HasGroupHeader => !string.IsNullOrEmpty(GroupHeader);
    public override string ToString() => Name;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Subtitle { get; init; } = string.Empty;
    public VaultItemKind Kind { get; init; }
    public bool Favorite { get; init; }
    public string? FolderId { get; init; }
    public string? OrganizationId { get; init; }
    public IReadOnlyList<string> CollectionIds { get; init; } = Array.Empty<string>();
    public bool CanEdit { get; init; } = true;
    public bool CanViewPassword { get; init; } = true;
    public bool CanDelete { get; init; } = true;
    public bool CanRestore { get; init; } = true;
    public bool CanModify => CanEdit;
    public bool CanFavorite => !IsDeleted;
    public bool CanClone { get; init; }
    public bool CanTransferToOrganization { get; init; }
    public bool CanManageCollections { get; init; }
    public bool CanArchive { get; init; }
    public bool CanUnarchive { get; init; }
    // Segoe Fluent Icon glyph(类型图标)。登录类后续可换 favicon。
    public string Glyph { get; init; } = "";
    // 登录条目的网站 host(供 favicon);非登录/无 URI 为 null。
    public string? IconDomain { get; init; }
    // 是否在回收站。回收站项不出现在常规过滤结果中。
    public bool IsDeleted { get; init; }
    public bool IsArchived { get; init; }
    public bool ShowArchiveAction => CanArchive && !IsDeleted && !IsArchived;
    public bool ShowUnarchiveAction => CanUnarchive && !IsDeleted && IsArchived;
    // 高级搜索:建项时拼好的小写可搜文本(不含密码/隐藏字段值/CVV)。
    public string SearchHaystack { get; init; } = string.Empty;
    // 分面标记(建项时算)。
    public bool HasTotp { get; init; }
    public bool HasAttachment { get; init; }
    public bool HasUri { get; init; }
    // 排序键。
    public DateTimeOffset RevisionDate { get; init; }
    public DateTimeOffset CreationDate { get; init; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public string TypeLabel => Kind switch
    {
        VaultItemKind.Login => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1E2DF9C3075AE9E4),
        VaultItemKind.Card => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7BB5BFB842DCB7BF),
        VaultItemKind.Identity => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2A9C9E9976421A9D),
        VaultItemKind.Note => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3B0221743F79E8A4),
        VaultItemKind.Ssh => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBF18658907FB9C66),
        VaultItemKind.BankAccount => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC4C25EEB5DAF09EA),
        VaultItemKind.DriversLicense => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text61B7153092BCDCCB),
        VaultItemKind.Passport => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD24F1B9E50C3D3C8),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFEE60AE594C1109F),
    };
}
