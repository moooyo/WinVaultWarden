using System.Collections.ObjectModel;
using App.Services;
using App.ViewModels.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace App.ViewModels;

public partial class VaultViewModel : ObservableObject
{
    private readonly IVaultUiService _service;
    private readonly IClipboardService? _clipboard;

    public ObservableCollection<CipherListItem> Items { get; } = new();
    public ObservableCollection<FilterNode> Filters { get; } = new();
    public ObservableCollection<CipherListItem> FilteredItems { get; } = new();
    public IEnumerable<FilterNode> FolderFilters => Filters.Where(f => f.Kind == FilterKind.Folder);

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
    public string? SelectedFilterTag => TagForFilter(SelectedFilter);
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
        Detail = value is null ? null : _service.GetDetail(value.Id);
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(NoSelection));
    }

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

    partial void OnSelectedFilterChanged(FilterNode? value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(SelectedFilterTag));
    }

    private static string? TagForFilter(FilterNode? filter) => filter?.Kind switch
    {
        FilterKind.AllItems => "vault:allitems",
        FilterKind.Favorites => "vault:favorites",
        FilterKind.Trash => "vault:trash",
        FilterKind.Type when filter.TypeFilter is not null => $"vault:type:{filter.TypeFilter}",
        FilterKind.Folder when !string.IsNullOrWhiteSpace(filter.FolderId) => $"vault:folder:{filter.FolderId}",
        _ => null,
    };

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

        var draft = EditorDraft;
        var errors = draft.Validate();
        if (errors.Count > 0)
        {
            EditorError = string.Join(Environment.NewLine, errors);
            return false;
        }

        var detail = CreateDetail(draft);
        _service.AddCipher(detail, draft.FolderId);
        var item = _service.GetItems().First(i => i.Id == detail.Id);

        Items.Add(item);
        EnsureFilterCanShow(item);
        if (!SearchIncludesItem(item))
            SearchText = string.Empty;
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
        var id = $"local-{Guid.NewGuid():N}";
        var now = DateTimeOffset.Now;
        var folderName = FolderNameFor(draft.FolderId);
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
                FolderName = folderName,
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
                FolderName = folderName,
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
                FolderName = folderName,
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
                FolderName = folderName,
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
                FolderName = folderName,
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

    private string? FolderNameFor(string? folderId) =>
        string.IsNullOrWhiteSpace(folderId)
            ? null
            : Filters.FirstOrDefault(f => f.Kind == FilterKind.Folder && f.FolderId == folderId)?.Label;

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

    private bool SearchIncludesItem(CipherListItem item) =>
        string.IsNullOrWhiteSpace(SearchText)
        || item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
        || item.Subtitle.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

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
