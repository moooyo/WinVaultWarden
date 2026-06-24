using System.Collections.ObjectModel;
using App.Services;
using App.ViewModels.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace App.ViewModels;

public partial class VaultViewModel : ObservableObject
{
    private readonly IVaultUiService _service;
    private readonly Dictionary<string, CipherDetail> _createdDetails = new();
    private readonly IClipboardService? _clipboard;
    private int _nextLocalCipherId = 1;

    public ObservableCollection<CipherListItem> Items { get; } = new();
    public ObservableCollection<FilterNode> Filters { get; } = new();
    public ObservableCollection<CipherListItem> FilteredItems { get; } = new();

    [ObservableProperty] private CipherListItem? _selectedItem;
    [ObservableProperty] private CipherDetail? _detail;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private FilterNode? _selectedFilter;

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial CipherEditorDraft? EditorDraft { get; set; }

    [ObservableProperty]
    public partial string EditorError { get; set; } = string.Empty;

    public bool HasSelection => Detail is not null;
    public bool NoSelection => Detail is null;
    public string EditorTitle => EditorDraft?.Type switch
    {
        VaultItemKind.Login => "新增登录",
        VaultItemKind.Card => "新增支付卡",
        VaultItemKind.Identity => "新增身份",
        VaultItemKind.Note => "新增笔记",
        VaultItemKind.Ssh => "新增 SSH 密钥",
        _ => string.Empty,
    };

    public VaultViewModel(IVaultUiService service, IClipboardService? clipboard = null)
    {
        _service = service;
        _clipboard = clipboard;
        foreach (var it in service.GetItems()) Items.Add(it);
        foreach (var f in service.GetFilters()) Filters.Add(f);
        SelectedFilter = Filters.FirstOrDefault();
        ApplyFilter();
    }

    partial void OnSelectedItemChanged(CipherListItem? value)
    {
        Detail = value is null ? null : GetDetail(value.Id);
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(NoSelection));
    }

    private CipherDetail GetDetail(string id) =>
        _createdDetails.TryGetValue(id, out var created)
            ? created
            : _service.GetDetail(id);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public void SelectFilterByTag(string? tag)
    {
        SelectedFilter = FindFilterByTag(tag)
            ?? Filters.FirstOrDefault(f => f.Kind == FilterKind.AllItems)
            ?? Filters.FirstOrDefault();
    }

    private FilterNode? FindFilterByTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        return tag switch
        {
            "vault:allitems" => Filters.FirstOrDefault(f => f.Kind == FilterKind.AllItems),
            "vault:favorites" => Filters.FirstOrDefault(f => f.Kind == FilterKind.Favorites),
            "vault:trash" => Filters.FirstOrDefault(f => f.Kind == FilterKind.Trash),
            _ when tag.StartsWith("vault:type:", StringComparison.Ordinal) => FindTypeFilter(tag["vault:type:".Length..]),
            _ when tag.StartsWith("vault:folder:", StringComparison.Ordinal) => FindFolderFilter(tag["vault:folder:".Length..]),
            _ => null,
        };
    }

    private FilterNode? FindTypeFilter(string typeName) =>
        Enum.TryParse(typeName, out VaultItemKind type)
            ? Filters.FirstOrDefault(f => f.Kind == FilterKind.Type && f.TypeFilter == type)
            : null;

    private FilterNode? FindFolderFilter(string folderId) =>
        Filters.FirstOrDefault(f => f.Kind == FilterKind.Folder && f.FolderId == folderId);

    partial void OnSelectedFilterChanged(FilterNode? value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredItems.Clear();

        IEnumerable<CipherListItem> source = SelectedFilter?.Kind switch
        {
            FilterKind.Favorites => Items.Where(i => i.Favorite && !i.IsDeleted),
            FilterKind.Trash     => Items.Where(i => i.IsDeleted),
            FilterKind.Type      => Items.Where(i => i.Kind == SelectedFilter.TypeFilter && !i.IsDeleted),
            FilterKind.Folder    => Items.Where(i => i.FolderId == SelectedFilter.FolderId && !i.IsDeleted),
            _                    => Items.Where(i => !i.IsDeleted), // AllItems 及未选中
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
            source = source.Where(i => i.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                                    || i.Subtitle.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        foreach (var i in source) FilteredItems.Add(i);
    }

    public void BeginAdd(VaultItemKind type)
    {
        EditorDraft = CipherEditorDraft.CreateDefault(type);
        EditorError = string.Empty;
        IsEditing = true;
        SelectedItem = null;
        Detail = null;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(NoSelection));
        OnPropertyChanged(nameof(EditorTitle));
    }

    public void CancelEdit()
    {
        IsEditing = false;
        EditorDraft = null;
        EditorError = string.Empty;
        OnPropertyChanged(nameof(EditorTitle));
    }

    public void ChangeEditorType(VaultItemKind type)
    {
        if (EditorDraft is null)
            return;

        EditorDraft.Type = type;
        EditorError = string.Empty;
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(EditorDraft));
    }

    public bool SaveDraft()
    {
        if (EditorDraft is null)
            return false;

        var errors = EditorDraft.Validate();
        if (errors.Count > 0)
        {
            EditorError = string.Join(Environment.NewLine, errors);
            return false;
        }

        var detail = CreateDetail(EditorDraft);
        _createdDetails[detail.Id] = detail;

        var item = new CipherListItem
        {
            Id = detail.Id,
            Name = detail.Name,
            Kind = detail.Kind,
            Subtitle = SubtitleFor(detail),
            Glyph = GlyphFor(detail.Kind),
            Favorite = detail.Favorite,
            FolderId = detail.FolderName,
            IsDeleted = false,
        };

        Items.Add(item);
        EnsureFilterCanShow(item);
        ApplyFilter();
        SelectedItem = FilteredItems.FirstOrDefault(i => i.Id == item.Id) ?? item;

        IsEditing = false;
        EditorDraft = null;
        EditorError = string.Empty;
        OnPropertyChanged(nameof(EditorTitle));
        return true;
    }

    private CipherDetail CreateDetail(CipherEditorDraft draft)
    {
        var id = $"local-{_nextLocalCipherId++}";
        var now = DateTimeOffset.Now;
        var customFields = draft.CustomFields
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .Select(f => new CustomField(f.Name, f.Type == CipherEditorFieldType.Boolean ? f.BooleanValue.ToString() : f.Value))
            .ToArray();

        return draft.Type switch
        {
            VaultItemKind.Login => new LoginDetail
            {
                Id = id,
                Name = draft.Name.Trim(),
                FolderName = draft.FolderId,
                Username = EmptyToNull(draft.Login.Username),
                Password = EmptyToNull(draft.Login.Password),
                TotpSecret = EmptyToNull(draft.Login.Totp),
                Uri = EmptyToNull(draft.Login.Uris.FirstOrDefault()?.Uri),
                Notes = EmptyToNull(draft.Notes),
                CustomFields = customFields,
                Created = now,
                Edited = now,
                Favorite = draft.Favorite,
                Reprompt = draft.Reprompt,
            },
            VaultItemKind.Card => new CardDetail
            {
                Id = id,
                Name = draft.Name.Trim(),
                FolderName = draft.FolderId,
                Cardholder = EmptyToNull(draft.Card.CardholderName),
                Number = EmptyToNull(draft.Card.Number),
                Expiry = FormatExpiry(draft.Card.ExpMonth, draft.Card.ExpYear),
                Brand = EmptyToNull(draft.Card.Brand),
                Cvv = EmptyToNull(draft.Card.Code),
                Notes = EmptyToNull(draft.Notes),
                CustomFields = customFields,
                Created = now,
                Edited = now,
                Favorite = draft.Favorite,
                Reprompt = draft.Reprompt,
            },
            VaultItemKind.Identity => new IdentityDetail
            {
                Id = id,
                Name = draft.Name.Trim(),
                FolderName = draft.FolderId,
                FullName = EmptyToNull(JoinNonEmpty(draft.Identity.FirstName, draft.Identity.MiddleName, draft.Identity.LastName)),
                Email = EmptyToNull(draft.Identity.Email),
                Phone = EmptyToNull(draft.Identity.Phone),
                IdNumber = EmptyToNull(JoinNonEmpty(draft.Identity.Ssn, draft.Identity.PassportNumber, draft.Identity.LicenseNumber)),
                Address = EmptyToNull(JoinNonEmpty(draft.Identity.Address1, draft.Identity.Address2, draft.Identity.Address3, draft.Identity.City, draft.Identity.State, draft.Identity.PostalCode, draft.Identity.Country)),
                Notes = EmptyToNull(draft.Notes),
                CustomFields = customFields,
                Created = now,
                Edited = now,
                Favorite = draft.Favorite,
                Reprompt = draft.Reprompt,
            },
            VaultItemKind.Note => new NoteDetail
            {
                Id = id,
                Name = draft.Name.Trim(),
                FolderName = draft.FolderId,
                Content = EmptyToNull(draft.Notes),
                Notes = EmptyToNull(draft.Notes),
                CustomFields = customFields,
                Created = now,
                Edited = now,
                Favorite = draft.Favorite,
                Reprompt = draft.Reprompt,
            },
            VaultItemKind.Ssh => new SshDetail
            {
                Id = id,
                Name = draft.Name.Trim(),
                FolderName = draft.FolderId,
                PrivateKey = EmptyToNull(draft.SshKey.PrivateKey),
                PublicKey = EmptyToNull(draft.SshKey.PublicKey),
                Fingerprint = EmptyToNull(draft.SshKey.KeyFingerprint),
                Notes = EmptyToNull(draft.Notes),
                CustomFields = customFields,
                Created = now,
                Edited = now,
                Favorite = draft.Favorite,
                Reprompt = draft.Reprompt,
            },
            _ => throw new InvalidOperationException($"Unsupported cipher type: {draft.Type}"),
        };
    }

    private void EnsureFilterCanShow(CipherListItem item)
    {
        if (FilterIncludesItem(SelectedFilter, item))
            return;

        SelectedFilter = Filters.FirstOrDefault(f => f.Kind == FilterKind.AllItems) ?? Filters.FirstOrDefault();
    }

    private static bool FilterIncludesItem(FilterNode? filter, CipherListItem item) => filter?.Kind switch
    {
        FilterKind.Favorites => item.Favorite && !item.IsDeleted,
        FilterKind.Trash => item.IsDeleted,
        FilterKind.Type => item.Kind == filter.TypeFilter && !item.IsDeleted,
        FilterKind.Folder => item.FolderId == filter.FolderId && !item.IsDeleted,
        _ => !item.IsDeleted,
    };

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FormatExpiry(string month, string year)
    {
        var cleanMonth = EmptyToNull(month);
        var cleanYear = EmptyToNull(year);
        return (cleanMonth, cleanYear) switch
        {
            (null, null) => null,
            (not null, null) => cleanMonth,
            (null, not null) => cleanYear,
            _ => $"{cleanMonth}/{cleanYear}",
        };
    }

    private static string JoinNonEmpty(params string[] values) =>
        string.Join(" ", values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));

    private static string SubtitleFor(CipherDetail detail) => detail switch
    {
        LoginDetail login => login.Username ?? "",
        CardDetail card => card.Brand ?? "支付卡",
        IdentityDetail identity => identity.FullName ?? "身份",
        NoteDetail => "笔记",
        SshDetail => "SSH 密钥",
        _ => "",
    };

    private static string GlyphFor(VaultItemKind kind) => kind switch
    {
        VaultItemKind.Login => "",
        VaultItemKind.Card => "",
        VaultItemKind.Identity => "",
        VaultItemKind.Note => "",
        VaultItemKind.Ssh => "",
        _ => "",
    };

    [RelayCommand]
    private void Sync() { /* mock:占位,真实同步后续接入 */ }

    [RelayCommand]
    private void Add() => BeginAdd(VaultItemKind.Login);

    [RelayCommand]
    private void Copy(string? value)
    {
        if (!string.IsNullOrEmpty(value)) _clipboard?.SetText(value);
    }
}
