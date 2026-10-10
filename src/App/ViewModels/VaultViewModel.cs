using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Security.Cryptography;
using System.Text;
using App.Services;
using App.ViewModels.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Models;

namespace App.ViewModels;

// 页面级 Vault 写入/同步/附件操作的唯一门禁分类:同一时刻只有一种操作可持有 _operationGate,
// 其余冲突操作一律返回 Busy(见 RunOperationAsync)。BusyMessage 依此渲染页面级 live region 文案。
public enum VaultOperationKind
{
    Sync,
    ResolveCipherCreate,
    SaveCipher,
    DeleteCipher,
    ArchiveCipher,
    Folder,
    Batch,
    AttachmentUpload,
    AttachmentDownload,
    AttachmentDelete,
}

public sealed record VaultWorkspaceContext(
    string Search,
    VaultFacets Facets,
    VaultSortKey Sort,
    int TypeIndex,
    double VerticalOffset,
    string? FocusedItemId,
    bool SearchHadFocus);

public partial class VaultViewModel : ObservableObject, ISensitiveState
{
    private readonly IVaultUiService _service;
    private readonly IClipboardService? _clipboard;
    private readonly IAttachmentUiService? _attachments;
    private readonly ISavedSearchStore? _savedStore;
    private readonly IItemRepromptGate? _reprompt;
    private readonly VaultRecentUseStore? _recentUseStore;
    private readonly Dictionary<string, DateTimeOffset> _recentUses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset?> _recentOrderSnapshot = new(StringComparer.Ordinal);
    private DateTime _recentGroupDate = DateTime.Today;
    private readonly HashSet<INotifyPropertyChanged> _editorObservedObjects = [];
    private readonly HashSet<INotifyCollectionChanged> _editorObservedCollections = [];
    private byte[]? _editorBaseline;
    private string? _editingId;
    private byte[]? _editorPasswordBaseline;
    private bool _suppressApply;
    private bool _isRebuildingFilteredItems;
    private readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);
    private readonly AsyncOperationGate _operationGate = new();
    private CancellationTokenSource? _operationCts;
    private CipherCreatePending? _pendingCipherCreate;
    private string? _ownedPendingCipherCreateFingerprint;
    private CipherCreatePending? _explicitlyAbandonedPendingCipherCreate;
    private bool _showPendingCreateAbandonedWarning;
    private CipherSaveUiContext? _activeCipherSave;
    private long _lifecycleEpoch;
    private bool _acceptCipherCreateGateNotifications = true;
    private readonly Dictionary<string, VaultWorkspaceContext> _workspaceContexts = new(StringComparer.Ordinal);
    private double _workspaceVerticalOffset;
    private string? _workspaceFocusedItemId;
    private bool _workspaceSearchHadFocus;
    private CipherEditorDraft _emptyEditorDisplayDraft = CipherEditorDraft.CreateDefault(VaultItemKind.Login);
    private bool _suppressEditorPlacementWrites;

    private sealed record CipherSaveUiContext(
        long LifecycleEpoch,
        CipherEditorDraft Draft,
        bool IsCreate);

    public ObservableCollection<SavedSearchView> SavedViews { get; } = new();

    public ObservableCollection<CipherListItem> Items { get; } = new();
    public ObservableCollection<FilterNode> Filters { get; } = new();
    public ObservableCollection<CipherListItem> FilteredItems { get; } = new();
    public ObservableCollection<VaultListGroup> GroupedItems { get; } = new();
    public IReadOnlyList<FilterNode> FolderFilters => Filters.Where(f => f.Kind == FilterKind.Folder).ToList();

    public bool AutoSelectFirstResult { get; set; }

    [ObservableProperty]
    public partial bool IsSelectionMode { get; set; }

    public int SelectedCount => _selectedIds.Count;
    public IReadOnlyCollection<string> SelectedIds => _selectedIds;
    public bool HasSelection => IsSelectionMode && _selectedIds.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanModifySelectedItem))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedItem))]
    [NotifyPropertyChangedFor(nameof(CanRestoreSelectedItem))]
    [NotifyPropertyChangedFor(nameof(CanArchiveSelectedItem))]
    [NotifyPropertyChangedFor(nameof(CanUnarchiveSelectedItem))]
    [NotifyPropertyChangedFor(nameof(CanTransferSelectedItem))]
    [NotifyPropertyChangedFor(nameof(CanManageSelectedItemCollections))]
    [NotifyPropertyChangedFor(nameof(ShowSensitiveUnlock))]
    public partial CipherListItem? SelectedItem { get; set; }

    public bool CanModifySelectedItem => SelectedItem?.CanModify == true;
    public bool CanDeleteSelectedItem => CanModifySelectedItem && SelectedItem?.CanDelete == true;
    public bool CanRestoreSelectedItem => CanModifySelectedItem && SelectedItem?.CanRestore == true;
    public bool CanArchiveSelectedItem => CanArchiveItems
        && SelectedItem is { CanArchive: true, IsDeleted: false, IsArchived: false };
    public bool CanUnarchiveSelectedItem => CanUnarchiveItems
        && SelectedItem is { CanUnarchive: true, IsDeleted: false, IsArchived: true };
    public bool CanTransferSelectedItem =>
        SelectedItem is { IsDeleted: false, CanTransferToOrganization: true };
    public bool CanManageSelectedItemCollections =>
        SelectedItem is { IsDeleted: false, CanManageCollections: true };
    public bool CanModifySelection => HasSelection && _selectedIds.All(id =>
        Items.FirstOrDefault(item => item.Id == id)?.CanModify == true);
    public bool CanDeleteSelection => HasSelection && _selectedIds.All(id =>
        Items.FirstOrDefault(item => item.Id == id)?.CanDelete == true);
    public bool CanRestoreSelection => HasSelection && _selectedIds.All(id =>
        Items.FirstOrDefault(item => item.Id == id)?.CanRestore == true);
    public bool CanMoveSelection => HasSelection && _selectedIds.All(id =>
        Items.FirstOrDefault(item => item.Id == id) is { IsDeleted: false });
    public bool CanArchiveSelection => CanArchiveItems && HasSelection && _selectedIds.All(id =>
        Items.FirstOrDefault(item => item.Id == id) is
            { CanArchive: true, IsDeleted: false, IsArchived: false });
    public bool CanUnarchiveSelection => CanUnarchiveItems && HasSelection && _selectedIds.All(id =>
        Items.FirstOrDefault(item => item.Id == id) is
            { CanUnarchive: true, IsDeleted: false, IsArchived: true });
    public bool CanTransferSelectionToOrganization =>
        TryGetUniformOrganizationSelection(out var selected, out var organizationId)
        && organizationId is null
        && selected.All(item => item.CanTransferToOrganization)
        && string.IsNullOrEmpty(SelectionPlacementError);
    public bool CanManageSelectionCollections =>
        TryGetUniformOrganizationSelection(out var selected, out var organizationId)
        && organizationId is not null
        && selected.All(item => item.CanManageCollections)
        && string.IsNullOrEmpty(SelectionPlacementError);
    public string? SelectedOrganizationId =>
        TryGetUniformOrganizationSelection(out _, out var organizationId)
            ? organizationId
            : null;
    public string SelectionPlacementError
    {
        get
        {
            if (!HasSelection)
                return string.Empty;
            if (!TryGetUniformOrganizationSelection(out var selected, out var organizationId))
            {
                return "所选条目混合了个人密码库与组织条目，或来自不同组织。请仅选择全部个人条目或同一组织的条目。";
            }
            if (selected.Any(item => item.IsDeleted))
                return "回收站中的条目不能执行批量组织操作。";
            if (selected.Any(item => !IsPlacementTypeSupported(item.Kind)))
                return "所选条目包含当前服务器不支持写入的类型。";
            if (organizationId is null)
            {
                if (selected.Any(item => !item.CanEdit || !item.CanViewPassword))
                    return "所选条目包含只读条目或不可查看密码的条目。";
                if (selected.Any(item => !item.CanTransferToOrganization))
                    return "所选条目包含无法安全转移的附件，或当前没有可写入的组织集合。";
            }
            else if (selected.Any(item => !item.CanManageCollections))
            {
                return "所选条目包含当前账户无权管理集合的条目。";
            }
            return string.Empty;
        }
    }
    public bool HasSelectionPlacementError => !string.IsNullOrEmpty(SelectionPlacementError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateNewCipherTypes))]
    [NotifyPropertyChangedFor(nameof(CanArchiveItems))]
    [NotifyPropertyChangedFor(nameof(CanUnarchiveItems))]
    [NotifyPropertyChangedFor(nameof(CanArchiveSelectedItem))]
    [NotifyPropertyChangedFor(nameof(CanUnarchiveSelectedItem))]
    [NotifyPropertyChangedFor(nameof(CanArchiveSelection))]
    [NotifyPropertyChangedFor(nameof(CanUnarchiveSelection))]
    [NotifyPropertyChangedFor(nameof(ShowArchiveSelectionAction))]
    [NotifyPropertyChangedFor(nameof(ShowUnarchiveSelectionAction))]
    [NotifyPropertyChangedFor(nameof(CanSaveEditorDraft))]
    [NotifyPropertyChangedFor(nameof(CanTransferSelectionToOrganization))]
    [NotifyPropertyChangedFor(nameof(CanManageSelectionCollections))]
    [NotifyPropertyChangedFor(nameof(SelectionPlacementError))]
    [NotifyPropertyChangedFor(nameof(HasSelectionPlacementError))]
    public partial VaultCapabilities Capabilities { get; set; } = VaultCapabilities.None;

    public bool CanCreateNewCipherTypes => Capabilities.CanWriteNewCipherTypes;
    public bool CanArchiveItems => Capabilities.CanArchive;
    public bool CanUnarchiveItems => Capabilities.CanUnarchive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSelectionPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowSensitiveUnlock))]
    [NotifyPropertyChangedFor(nameof(ShowDetailNotes))]
    [NotifyPropertyChangedFor(nameof(ShowProtectedPasswordCopy))]
    [NotifyPropertyChangedFor(nameof(DetailLocation))]
    [NotifyPropertyChangedFor(nameof(DetailWebsite))]
    [NotifyPropertyChangedFor(nameof(HasDetailWebsite))]
    public partial CipherDetail? Detail { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedTypeIndex { get; set; }

    partial void OnSelectedTypeIndexChanged(int value)
    {
        if (!_suppressApply) ApplyFilter();
    }

    public string WorkspaceTitle => SelectedFilter?.Kind == FilterKind.AllItems
        ? "全部条目" : SelectedFilter?.Label ?? "全部条目";
    public string VisibleItemCountText => $"{FilteredItems.Count} 个条目";
    public bool ShowDetailNotes => Detail is not NoteDetail && !string.IsNullOrWhiteSpace(Detail?.Notes);
    public bool ShowReadOnlyNotice => SelectedItem is { CanModify: false, IsDeleted: false };
    public bool ShowHiddenSecretsNotice => SelectedItem is { CanViewPassword: false };
    public bool ShowProtectedPasswordCopy => ShowSensitiveUnlock && SelectedItem?.Kind == VaultItemKind.Login;
    public string DetailLocation
    {
        get
        {
            var owner = SelectedItem?.OrganizationId is { } organizationId
                ? Filters.FirstOrDefault(filter => filter.Kind == FilterKind.Organization
                    && filter.OrganizationId == organizationId)?.Label ?? "组织密码库"
                : "个人密码库";
            return string.IsNullOrWhiteSpace(Detail?.FolderName) ? owner : $"{owner} › {Detail.FolderName}";
        }
    }
    public string DetailWebsite => Detail is LoginDetail login
        ? login.DisplayUris.FirstOrDefault(value => System.Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "https" or "http") ?? string.Empty
        : string.Empty;
    public bool HasDetailWebsite => !string.IsNullOrEmpty(DetailWebsite);
    public string EmptyStateTitle => SelectedFilter?.Kind switch
    {
        FilterKind.Trash => "回收站是空的",
        FilterKind.Archive => "没有归档的条目",
        _ => "这里还没有条目",
    };
    public string NoResultsTitle => string.IsNullOrWhiteSpace(SearchText)
        ? "没有符合筛选条件的条目"
        : $"没有找到“{SearchText.Trim()}”";
    public string NewSearchItemLabel => string.IsNullOrWhiteSpace(SearchText)
        ? "新建条目"
        : $"新建“{SearchText.Trim()}”";
    public VaultWorkspaceContext WorkspaceContext => new(
        SearchText, CurrentFacets, SelectedSort, SelectedTypeIndex,
        _workspaceVerticalOffset, _workspaceFocusedItemId, _workspaceSearchHadFocus);

    public void RememberWorkspaceContext(double verticalOffset, string? focusedItemId, bool searchHadFocus)
    {
        if (Filters.Count == 0)
            return;
        _workspaceVerticalOffset = double.IsFinite(verticalOffset) ? Math.Max(0, verticalOffset) : 0;
        _workspaceFocusedItemId = focusedItemId;
        _workspaceSearchHadFocus = searchHadFocus;
        RememberCurrentScope();
    }

    private void RememberCurrentScope()
    {
        if (SelectedFilterTag is { } tag && Filters.Count > 0)
            _workspaceContexts[tag] = WorkspaceContext;
    }

    public void RestoreWorkspaceContext(string? tag = null)
    {
        RefreshRecentUseSnapshot();
        if (tag?.StartsWith("vault:item:", StringComparison.Ordinal) == true)
        {
            ApplyFilter();
            OpenItemFromNavigation(tag["vault:item:".Length..]);
            return;
        }
        if (tag is not null)
            SelectFilterByTag(tag);
        ApplyFilter();
        // A return to the workspace restores results without reopening a credential.
        if (!IsEditing)
            SelectedItem = null;
        OnPropertyChanged(nameof(WorkspaceContext));
    }

    private void OpenItemFromNavigation(string id)
    {
        if (IsEditing)
        {
            OperationError = "请先保存或取消当前编辑，再查看其他条目。";
            return;
        }
        var item = Items.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        if (item is null)
        {
            SelectedItem = null;
            OperationError = "该条目已不在当前保险库中，请同步后重试。";
            return;
        }

        if (!FilteredItems.Any(candidate => candidate.Id == id))
        {
            SelectFilterByTag(item.IsDeleted ? "vault:trash" : item.IsArchived ? "vault:archive" : "vault:allitems");
            _suppressApply = true;
            try
            {
                SearchText = string.Empty;
                SelectedTypeIndex = 0;
                FacetTotp = false;
                FacetAttachment = false;
                FacetUri = false;
                FacetFavoriteOnly = false;
            }
            finally
            {
                _suppressApply = false;
            }
            ApplyFilter();
        }
        OperationError = string.Empty;
        SelectedItem = FilteredItems.FirstOrDefault(candidate => candidate.Id == id);
    }

    public void ReturnToResults()
    {
        if (!CanCancelEditor)
            return;
        CancelEdit();
        SelectedItem = null;
        OnPropertyChanged(nameof(WorkspaceContext));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFacetCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveFacets))]
    public partial bool FacetTotp { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFacetCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveFacets))]
    public partial bool FacetAttachment { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFacetCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveFacets))]
    public partial bool FacetUri { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFacetCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveFacets))]
    public partial bool FacetFavoriteOnly { get; set; }
    [ObservableProperty]
    public partial VaultSortKey SelectedSort { get; set; } = VaultSortKey.RecentUse;

    public VaultFacets CurrentFacets => new(FacetTotp, FacetAttachment, FacetUri, FacetFavoriteOnly);
    public bool HasActiveRefinement => CurrentFacets.Any || SelectedTypeIndex > 0 || !string.IsNullOrWhiteSpace(SearchText);

    // 分面筛选徽标:激活的分面个数(0–4)与是否有激活分面,供“筛选 ▾”按钮的 InfoBadge 显示。
    public int ActiveFacetCount =>
        (FacetTotp ? 1 : 0) + (FacetAttachment ? 1 : 0) + (FacetUri ? 1 : 0) + (FacetFavoriteOnly ? 1 : 0);
    public bool HasActiveFacets => CurrentFacets.Any;

    private static readonly VaultSortKey[] DisplaySortOrder =
        [VaultSortKey.RecentUse, VaultSortKey.NameAsc, VaultSortKey.RevisionDesc, VaultSortKey.NameDesc, VaultSortKey.CreationDesc];

    /// <summary>Maps the visual sort menu to stable persisted enum values.</summary>
    public int SelectedSortIndex
    {
        get => Math.Max(0, Array.IndexOf(DisplaySortOrder, SelectedSort));
        set { if (value >= 0 && value < DisplaySortOrder.Length) SelectedSort = DisplaySortOrder[value]; }
    }

    partial void OnFacetTotpChanged(bool value) { if (!_suppressApply) ApplyFilter(); }
    partial void OnFacetAttachmentChanged(bool value) { if (!_suppressApply) ApplyFilter(); }
    partial void OnFacetUriChanged(bool value) { if (!_suppressApply) ApplyFilter(); }
    partial void OnFacetFavoriteOnlyChanged(bool value) { if (!_suppressApply) ApplyFilter(); }
    partial void OnSelectedSortChanged(VaultSortKey value)
    {
        OnPropertyChanged(nameof(SelectedSortIndex));
        RefreshRecentUseSnapshot();
        if (!_suppressApply) ApplyFilter();
    }

    [ObservableProperty]
    public partial FilterNode? SelectedFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSelectionPlaceholder))]
    [NotifyPropertyChangedFor(nameof(HasUnsavedEditorChanges))]
    public partial bool IsEditing { get; set; }

    public bool HasUnsavedEditorChanges => IsEditing && EditorDraft is { } draft
        && (_editingId is null ? HasNewDraftContent(draft)
            : _editorBaseline is null || !FingerprintDraft(draft).AsSpan().SequenceEqual(_editorBaseline));

    public bool HasEditorPasswordChange
    {
        get
        {
            if (!IsEditing || _editingId is null || EditorDraft is not { IsLogin: true } draft || _editorPasswordBaseline is null)
                return false;
            var current = FingerprintPassword(draft.Login.Password);
            try { return !CryptographicOperations.FixedTimeEquals(current, _editorPasswordBaseline); }
            finally { CryptographicOperations.ZeroMemory(current); }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorSelectedFolder))]
    [NotifyPropertyChangedFor(nameof(EditorSelectedDestination))]
    [NotifyPropertyChangedFor(nameof(CanSaveEditorDraft))]
    [NotifyPropertyChangedFor(nameof(HasValidEditorDraft))]
    [NotifyPropertyChangedFor(nameof(EditorDisplayDraft))]
    public partial CipherEditorDraft? EditorDraft { get; set; }

    // A non-null presentation source resets every bound field when the business draft is discarded.
    public CipherEditorDraft EditorDisplayDraft => EditorDraft ?? _emptyEditorDisplayDraft;

    public bool HasValidEditorDraft => EditorDraft?.HasRequiredData() == true;

    public void RefreshEditorBindings(Action refreshBindings)
    {
        var wasSuppressed = _suppressEditorPlacementWrites;
        _suppressEditorPlacementWrites = true;
        try { refreshBindings(); }
        finally { _suppressEditorPlacementWrites = wasSuppressed; }
    }

    partial void OnEditorDraftChanged(CipherEditorDraft? oldValue, CipherEditorDraft? newValue)
    {
        if (newValue is null)
            _emptyEditorDisplayDraft = CipherEditorDraft.CreateDefault(VaultItemKind.Login);
        ObserveEditorDraft(newValue);
        ResetEditorBaseline();
    }

    private void OnEditorRequirementChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasValidEditorDraft));
        OnPropertyChanged(nameof(HasUnsavedEditorChanges));
    }

    private void OnEditorCollectionRequirementChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ObserveEditorDraft(EditorDraft);
        OnPropertyChanged(nameof(HasValidEditorDraft));
        OnPropertyChanged(nameof(HasUnsavedEditorChanges));
    }

    private void ObserveEditorDraft(CipherEditorDraft? draft)
    {
        foreach (var value in _editorObservedObjects)
            value.PropertyChanged -= OnEditorRequirementChanged;
        foreach (var value in _editorObservedCollections)
            value.CollectionChanged -= OnEditorCollectionRequirementChanged;
        _editorObservedObjects.Clear();
        _editorObservedCollections.Clear();
        if (draft is null)
            return;

        _editorObservedObjects.UnionWith(new INotifyPropertyChanged[]
        {
            draft, draft.Login, draft.Card, draft.Identity, draft.SecureNote,
            draft.SshKey, draft.BankAccount, draft.DriversLicense, draft.Passport,
        });
        _editorObservedObjects.UnionWith(draft.Login.Uris);
        _editorObservedObjects.UnionWith(draft.CustomFields);
        _editorObservedCollections.UnionWith(new INotifyCollectionChanged[]
        {
            draft.CollectionIds, draft.Login.Uris, draft.CustomFields,
        });
        foreach (var value in _editorObservedObjects)
            value.PropertyChanged += OnEditorRequirementChanged;
        foreach (var value in _editorObservedCollections)
            value.CollectionChanged += OnEditorCollectionRequirementChanged;
    }

    private void ResetEditorBaseline()
    {
        if (_editorBaseline is not null)
            CryptographicOperations.ZeroMemory(_editorBaseline);
        _editorBaseline = EditorDraft is { } draft ? FingerprintDraft(draft) : null;
        if (_editorPasswordBaseline is not null)
            CryptographicOperations.ZeroMemory(_editorPasswordBaseline);
        _editorPasswordBaseline = EditorDraft is { IsLogin: true } loginDraft
            ? FingerprintPassword(loginDraft.Login.Password) : null;
        OnPropertyChanged(nameof(HasUnsavedEditorChanges));
    }

    private static byte[] FingerprintPassword(string? password)
    {
        var bytes = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(password) ? string.Empty : password);
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool HasNewDraftContent(CipherEditorDraft draft)
    {
        static bool HasValue(params string?[] values) => values.Any(value => !string.IsNullOrEmpty(value));

        // A generated password or copied item is already valuable even before the first edit.
        // Placement defaults and an empty initial URI row do not make a new draft dirty.
        return !string.IsNullOrWhiteSpace(draft.Name)
            || HasValue(draft.Notes, draft.Login.Username, draft.Login.Password, draft.Login.Totp)
            || draft.Login.Uris.Any(uri => !string.IsNullOrEmpty(uri.Uri))
            || HasValue(draft.Card.CardholderName, draft.Card.Number, draft.Card.ExpMonth,
                draft.Card.ExpYear, draft.Card.Code, draft.Card.Brand)
            || HasValue(draft.Identity.Title, draft.Identity.FirstName, draft.Identity.MiddleName,
                draft.Identity.LastName, draft.Identity.Username, draft.Identity.Company,
                draft.Identity.Ssn, draft.Identity.PassportNumber, draft.Identity.LicenseNumber,
                draft.Identity.Email, draft.Identity.Phone, draft.Identity.Address1,
                draft.Identity.Address2, draft.Identity.Address3, draft.Identity.City,
                draft.Identity.State, draft.Identity.PostalCode, draft.Identity.Country)
            || HasValue(draft.SshKey.PrivateKey, draft.SshKey.PublicKey, draft.SshKey.KeyFingerprint)
            || HasValue(draft.BankAccount.BankName, draft.BankAccount.NameOnAccount, draft.BankAccount.AccountType,
                draft.BankAccount.AccountNumber, draft.BankAccount.RoutingNumber, draft.BankAccount.BranchNumber,
                draft.BankAccount.Pin, draft.BankAccount.SwiftCode, draft.BankAccount.Iban, draft.BankAccount.BankContactPhone)
            || HasValue(draft.DriversLicense.FirstName, draft.DriversLicense.MiddleName, draft.DriversLicense.LastName,
                draft.DriversLicense.DateOfBirth, draft.DriversLicense.LicenseNumber, draft.DriversLicense.IssuingCountry,
                draft.DriversLicense.IssuingState, draft.DriversLicense.IssueDate, draft.DriversLicense.ExpirationDate,
                draft.DriversLicense.IssuingAuthority, draft.DriversLicense.LicenseClass)
            || HasValue(draft.Passport.Surname, draft.Passport.GivenName, draft.Passport.DateOfBirth,
                draft.Passport.Sex, draft.Passport.BirthPlace, draft.Passport.Nationality,
                draft.Passport.IssuingCountry, draft.Passport.PassportNumber, draft.Passport.PassportType,
                draft.Passport.NationalIdentificationNumber, draft.Passport.IssuingAuthority,
                draft.Passport.IssueDate, draft.Passport.ExpirationDate)
            || draft.CustomFields.Any(field => HasValue(field.Name, field.Value)
                || field.BooleanValue || field.LinkedId is not null);
    }

    private static byte[] FingerprintDraft(CipherEditorDraft draft)
    {
        // Retain only a digest for dirty tracking; do not retain a second plaintext draft.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        void Write(params string?[] values)
        {
            foreach (var value in values)
                writer.Write(value ?? string.Empty);
        }
        writer.Write((int)draft.Type);
        Write(draft.Name, draft.FolderId, draft.OrganizationId, draft.CollectionId, draft.Notes);
        writer.Write(draft.Favorite);
        writer.Write(draft.Reprompt);
        writer.Write(draft.CollectionIds.Count);
        foreach (var collection in draft.CollectionIds)
            Write(collection);
        Write(draft.Login.Username, draft.Login.Password, draft.Login.Totp);
        writer.Write(draft.Login.Uris.Count);
        foreach (var uri in draft.Login.Uris)
        {
            Write(uri.Uri);
            writer.Write(uri.Match ?? -1);
        }
        Write(draft.Card.CardholderName, draft.Card.Number, draft.Card.ExpMonth,
            draft.Card.ExpYear, draft.Card.Code, draft.Card.Brand);
        Write(draft.Identity.Title, draft.Identity.FirstName, draft.Identity.MiddleName,
            draft.Identity.LastName, draft.Identity.Username, draft.Identity.Company,
            draft.Identity.Ssn, draft.Identity.PassportNumber, draft.Identity.LicenseNumber,
            draft.Identity.Email, draft.Identity.Phone, draft.Identity.Address1,
            draft.Identity.Address2, draft.Identity.Address3, draft.Identity.City,
            draft.Identity.State, draft.Identity.PostalCode, draft.Identity.Country);
        writer.Write(draft.SecureNote.Type);
        Write(draft.SshKey.PrivateKey, draft.SshKey.PublicKey, draft.SshKey.KeyFingerprint);
        Write(draft.BankAccount.BankName, draft.BankAccount.NameOnAccount, draft.BankAccount.AccountType,
            draft.BankAccount.AccountNumber, draft.BankAccount.RoutingNumber, draft.BankAccount.BranchNumber,
            draft.BankAccount.Pin, draft.BankAccount.SwiftCode, draft.BankAccount.Iban, draft.BankAccount.BankContactPhone);
        Write(draft.DriversLicense.FirstName, draft.DriversLicense.MiddleName, draft.DriversLicense.LastName,
            draft.DriversLicense.DateOfBirth, draft.DriversLicense.LicenseNumber, draft.DriversLicense.IssuingCountry,
            draft.DriversLicense.IssuingState, draft.DriversLicense.IssueDate, draft.DriversLicense.ExpirationDate,
            draft.DriversLicense.IssuingAuthority, draft.DriversLicense.LicenseClass);
        Write(draft.Passport.Surname, draft.Passport.GivenName, draft.Passport.DateOfBirth,
            draft.Passport.Sex, draft.Passport.BirthPlace, draft.Passport.Nationality,
            draft.Passport.IssuingCountry, draft.Passport.PassportNumber, draft.Passport.PassportType,
            draft.Passport.NationalIdentificationNumber, draft.Passport.IssuingAuthority,
            draft.Passport.IssueDate, draft.Passport.ExpirationDate);
        writer.Write(draft.CustomFields.Count);
        foreach (var field in draft.CustomFields)
        {
            Write(field.Name, field.Value);
            writer.Write((int)field.Type);
            writer.Write(field.BooleanValue);
            writer.Write(field.LinkedId ?? -1);
            writer.Write(field.RawType ?? -1);
            writer.Write(field.PreserveWhenNameBlank);
        }
        writer.Flush();
        var buffer = stream.GetBuffer().AsSpan(0, checked((int)stream.Length));
        try { return SHA256.HashData(buffer); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorSelectedDestination))]
    public partial IReadOnlyList<CipherDestinationOption> EditorDestinations { get; set; } =
        new ObservableCollection<CipherDestinationOption> { CipherDestinationOption.Personal };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEditorCollectionOptions))]
    public partial IReadOnlyList<CipherCollectionOption> EditorCollectionOptions { get; set; } =
        new ObservableCollection<CipherCollectionOption>();

    private bool _canChooseEditorDestination;
    public bool CanChooseEditorDestination => _canChooseEditorDestination;
    public bool ShowEditorCollectionOptions =>
        CanChooseEditorDestination
        && EditorDraft?.OrganizationId is not null
        && EditorCollectionOptions.Count > 0;

    public CipherDestinationOption? EditorSelectedDestination
    {
        get
        {
            if (EditorDraft is null)
                return null;
            return EditorDestinations.FirstOrDefault(destination =>
                string.Equals(destination.OrganizationId, EditorDraft.OrganizationId, StringComparison.Ordinal));
        }
        set
        {
            if (EditorDraft is null || !_canChooseEditorDestination || value is null
                || _suppressEditorPlacementWrites || _isRebuildingFilteredItems)
                return;
            var organizationChanged = !string.Equals(
                EditorDraft.OrganizationId,
                value.OrganizationId,
                StringComparison.Ordinal);
            if (!organizationChanged)
                return;
            EditorDraft.OrganizationId = value.OrganizationId;
            EditorDraft.ReplaceCollectionIds([]);
            EditorCollectionOptions = value.OrganizationId is null
                ? new ObservableCollection<CipherCollectionOption>()
                : new ObservableCollection<CipherCollectionOption>(BuildWritableNewCipherCollectionOptions(
                    value.OrganizationId,
                    EditorDraft.CollectionIds));
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowEditorCollectionOptions));
        }
    }

    // 文件夹选择:以 FilterNode 形式暴露给 ComboBox 的 SelectedItem,内部映射到 EditorDraft.FolderId。
    // 用 x:Bind + SelectedItem 取代反射式 DisplayMemberPath/SelectedValuePath(AOT 下反射会抛
    // NotSupportedException)。getter 返回的是 FolderFilters 中的同一实例,故 SelectedItem 能按引用命中。
    public FilterNode? EditorSelectedFolder
    {
        get => EditorDraft is null
            ? null
            : FolderFilters.FirstOrDefault(f => f.FolderId == EditorDraft.FolderId);
        set
        {
            if (EditorDraft is not null && !_suppressEditorPlacementWrites && !_isRebuildingFilteredItems
                && !string.Equals(EditorDraft.FolderId, value?.FolderId, StringComparison.Ordinal))
                EditorDraft.FolderId = value?.FolderId;
        }
    }

    [ObservableProperty]
    public partial string EditorError { get; set; } = string.Empty;

    // 页面级唯一 Vault 操作状态:非 null 表示当前有写入/同步/附件操作占用 _operationGate。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(BusyMessage))]
    [NotifyPropertyChangedFor(nameof(CanStartVaultOperation))]
    [NotifyPropertyChangedFor(nameof(CanSaveEditorDraft))]
    [NotifyPropertyChangedFor(nameof(CanCancelEditor))]
    [NotifyPropertyChangedFor(nameof(CanBeginCipherCreate))]
    [NotifyPropertyChangedFor(nameof(CanEditEditorDraft))]
    [NotifyPropertyChangedFor(nameof(CanChangeEditorType))]
    [NotifyPropertyChangedFor(nameof(CanCheckPendingCipherCreate))]
    [NotifyPropertyChangedFor(nameof(CanAbandonPendingCipherCreate))]
    public partial VaultOperationKind? CurrentOperation { get; set; }

    public bool IsBusy => CurrentOperation is not null;
    public bool CanStartVaultOperation => CurrentOperation is null;
    public bool CanSaveEditorDraft => CanStartVaultOperation
        && !HasOwnedPendingCipherCreate
        && (_editingId is not null || !_service.HasPendingOrInFlightCipherCreate())
        && (EditorDraft is null
            || !IsNewCipherType(EditorDraft.Type)
            || CanCreateNewCipherTypes);
    public bool CanCancelEditor => CanStartVaultOperation && !HasOwnedPendingCipherCreate;
    public bool CanBeginCipherCreate => CanStartVaultOperation
        && _pendingCipherCreate is null
        && !_service.HasPendingOrInFlightCipherCreate();
    public bool CanEditEditorDraft => CanStartVaultOperation
        && !HasOwnedPendingCipherCreate;
    public bool HasPendingCipherCreate => _pendingCipherCreate is not null;
    public bool CanCheckPendingCipherCreate => HasPendingCipherCreate && CanStartVaultOperation;
    public bool CanAbandonPendingCipherCreate => HasPendingCipherCreate && CanStartVaultOperation;
    public bool HasRecoverablePendingDraft =>
        HasOwnedPendingCipherCreate && IsEditing && EditorDraft is not null;
    public string PendingCipherCreateTitle => _pendingCipherCreate?.CommitConfirmed == true
        ? "服务器已接受创建，但仍无法定位条目"
        : "无法确认上一次新建是否成功";
    public string PendingCipherCreateMessage => _pendingCipherCreate?.CommitConfirmed == true
        ? "服务器曾返回成功，但客户端仍无法确认新条目。解除防重复保护后再次保存，很可能产生重复条目。"
        : "服务器可能已经保存了该条目。解除防重复保护后再次保存，可能产生重复条目。";
    public string PendingCipherCreateDraftMessage => HasRecoverablePendingDraft
        ? "与此请求对应的当前草稿仍保留。放弃待确认状态后，请先检查密码库，再决定是否手动保存。"
        : "本地没有与此请求对应的可恢复草稿；此操作可能来自浏览器或上一次应用运行。";
    private bool HasOwnedPendingCipherCreate =>
        _pendingCipherCreate is not null && OwnsPendingCipherCreate(_pendingCipherCreate);
    public string BusyMessage => CurrentOperation switch
    {
        VaultOperationKind.Sync => "正在同步保险库…",
        VaultOperationKind.ResolveCipherCreate => "正在更新待确认状态…",
        VaultOperationKind.SaveCipher => "正在保存条目…",
        VaultOperationKind.DeleteCipher => "正在更新回收站…",
        VaultOperationKind.ArchiveCipher => "正在更新归档…",
        VaultOperationKind.Folder => "正在更新文件夹…",
        VaultOperationKind.Batch => "正在处理所选条目…",
        VaultOperationKind.AttachmentUpload => "正在上传附件…",
        VaultOperationKind.AttachmentDownload => "正在下载附件…",
        VaultOperationKind.AttachmentDelete => "正在删除附件…",
        _ => string.Empty,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationError))]
    public partial string OperationError { get; set; } = string.Empty;

    // InfoBar.IsOpen 需要 bool;OperationError 是 string,直接用转换器会因返回 Visibility 而绑定失败。
    public bool HasOperationError => !string.IsNullOrEmpty(OperationError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationWarning))]
    [NotifyPropertyChangedFor(nameof(ShowOperationWarningInfo))]
    [NotifyPropertyChangedFor(nameof(ShowDataStaleInfo))]
    [NotifyPropertyChangedFor(nameof(OperationWarningTitle))]
    public partial string OperationWarning { get; set; } = string.Empty;

    public bool HasOperationWarning => !string.IsNullOrEmpty(OperationWarning);
    public bool ShowOperationWarningInfo => HasOperationWarning && !HasPendingCipherCreate;
    public string OperationWarningTitle => _pendingCipherCreate is not null
        ? "保存结果待确认"
        : _showPendingCreateAbandonedWarning
            ? "待确认状态已放弃"
            : "已保存，需要同步";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkippedCiphers))]
    [NotifyPropertyChangedFor(nameof(SkippedCipherMessage))]
    public partial int SkippedCipherCount { get; set; }

    // 同步时有条目无法解密被跳过 → 提示用户这些条目已被隐藏。
    public bool HasSkippedCiphers => SkippedCipherCount > 0;
    public string SkippedCipherMessage => $"{SkippedCipherCount} 个条目无法解密，已隐藏";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsupportedOrganizationCiphers))]
    [NotifyPropertyChangedFor(nameof(UnsupportedOrganizationCipherMessage))]
    public partial int UnsupportedOrganizationCipherCount { get; set; }

    public bool HasUnsupportedOrganizationCiphers => UnsupportedOrganizationCipherCount > 0;
    public string UnsupportedOrganizationCipherMessage =>
        $"{UnsupportedOrganizationCipherCount} 个共享条目因组织密钥无法解密，已隐藏。";

    // 当前快照来自离线缓存(未联网同步) → 提示用户数据可能已过期。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDataStaleInfo))]
    public partial bool DataStale { get; set; }

    public bool ShowDataStaleInfo => DataStale && !HasOperationWarning;

    public bool HasDetailSelected => Detail is not null;
    public bool NoSelection => Detail is null;

    // 详情要求主密码重提示且当前投影尚未解锁 → 显示"验证后显示敏感字段"入口。
    public bool ShowSensitiveUnlock =>
        SelectedItem?.CanViewPassword == true
        && Detail is { Reprompt: true, SensitiveUnlocked: false };

    // 右栏“未选择任何项目”空状态浮层:无选中且不在编辑(新建条目时 Detail 为空但在编辑,需排除)。
    public bool ShowNoSelectionPlaceholder => Detail is null && !IsEditing;

    // 详情头 favicon 域名(仅登录详情有值)。
    public string? DetailIconDomain =>
        Detail is LoginDetail login ? Core.IconDomain.Extract(login.Uri) : null;
    public string? SelectedFilterTag => TagForFilter(SelectedFilter);
    public bool IsSelectedItemDeleted => SelectedItem?.IsDeleted == true;
    public bool IsSelectedItemArchived => SelectedItem?.IsArchived == true;
    public bool IsFolderFilterSelected => SelectedFilter?.Kind == FilterKind.Folder;
    public bool IsTrashFilterSelected => SelectedFilter?.Kind == FilterKind.Trash;
    public bool IsArchiveFilterSelected => SelectedFilter?.Kind == FilterKind.Archive;
    public bool ShowArchiveSelectionAction =>
        CanArchiveItems && !IsTrashFilterSelected && !IsArchiveFilterSelected;
    public bool ShowUnarchiveSelectionAction => CanUnarchiveItems && IsArchiveFilterSelected;

    public int TrashCount => Items.Count(i => i.IsDeleted);
    public bool HasTrashItems => TrashCount > 0;
    public bool IsTrashEmpty => IsTrashFilterSelected && TrashCount == 0;
    public bool CanEmptyRecycleBin => IsTrashFilterSelected && HasTrashItems;
    public string TrashHeader => $"回收站 ({TrashCount})";
    public int ArchiveCount => Items.Count(i => i.IsArchived && !i.IsDeleted);
    public bool IsArchiveEmpty => IsArchiveFilterSelected && ArchiveCount == 0;

    // An empty navigation scope uses the creation prompt when no refinements are active.
    public bool HasNoItems => !IsTrashFilterSelected && !IsArchiveFilterSelected
        && FilteredItems.Count == 0 && !HasActiveRefinement;
    // Search and facet misses remain distinct from an empty navigation scope.
    public bool NoResults => FilteredItems.Count == 0 && !HasNoItems && !IsTrashEmpty && !IsArchiveEmpty;

    public event EventHandler? FoldersChanged;
    public event EventHandler? SelectionRebindRequested;

    public bool IsRebuildingItems => _isRebuildingFilteredItems;

    public bool CanChangeEditorType => _editingId is null && CanEditEditorDraft;

    public string EditorTitle => EditorDraft?.Type switch
    {
        VaultItemKind.Login => _editingId is null ? "新增登录" : "编辑登录",
        VaultItemKind.Card => _editingId is null ? "新增支付卡" : "编辑支付卡",
        VaultItemKind.Identity => _editingId is null ? "新增身份" : "编辑身份",
        VaultItemKind.Note => _editingId is null ? "新增笔记" : "编辑笔记",
        VaultItemKind.Ssh => _editingId is null ? "新增 SSH 密钥" : "编辑 SSH 密钥",
        VaultItemKind.BankAccount => _editingId is null ? "新增银行账户" : "编辑银行账户",
        VaultItemKind.DriversLicense => _editingId is null ? "新增驾驶证" : "编辑驾驶证",
        VaultItemKind.Passport => _editingId is null ? "新增护照" : "编辑护照",
        _ => string.Empty,
    };

    public VaultViewModel(IVaultUiService service, IClipboardService? clipboard = null, IAttachmentUiService? attachments = null,
        ISavedSearchStore? savedStore = null, IItemRepromptGate? reprompt = null, Vault.VaultSession? session = null)
    {
        _service = service;
        _clipboard = clipboard;
        _attachments = attachments;
        _savedStore = savedStore;
        _reprompt = reprompt;
        _recentUseStore = VaultRecentUseStore.CreateForSession(session);
        SubscribeToCipherCreateGateChanges(service);
        Capabilities = service.Capabilities;
        foreach (var it in service.GetItems()) Items.Add(it);
        RefreshRecentUseSnapshot();
        foreach (var f in service.GetFilters()) Filters.Add(f);
        AddSupplementaryFilters();
        SkippedCipherCount = service.SkippedCipherCount;
        UnsupportedOrganizationCipherCount = service.UnsupportedOrganizationCipherCount;
        DataStale = service.DataStale;
        if (service.GetPendingCipherCreate() is { } pending)
        {
            _pendingCipherCreate = pending;
            DataStale = true;
            OperationWarning = "新条目的保存结果仍待确认。请先同步，不要重复提交。";
            OperationError = "检测到此账户存在待确认的新建操作。";
        }
        if (savedStore is not null)
            foreach (var v in savedStore.GetAll()) SavedViews.Add(v);
        SelectedFilter = Filters.FirstOrDefault();
        ApplyFilter();
    }

    public SavedSearchView CaptureCurrentView(string name) =>
        new(name, SelectedFilterTag, CurrentFacets, SearchText, SelectedSort, SelectedTypeIndex);

    [RelayCommand]
    private void SaveCurrentView(string name)
    {
        if (_savedStore is null || string.IsNullOrWhiteSpace(name)) return;
        var view = CaptureCurrentView(name.Trim());
        _savedStore.Save(view);
        var existing = SavedViews.FirstOrDefault(v => v.Name == view.Name);
        if (existing is not null) SavedViews.Remove(existing);
        SavedViews.Add(view);
    }

    [RelayCommand]
    private void ApplyView(SavedSearchView? view)
    {
        if (view is null) return;
        _suppressApply = true;
        try
        {
            SelectFilterByTag(view.NavTag);                 // 失效文件夹 → 回退 AllItems(既有行为)
            FacetTotp = view.Facets.Totp;
            FacetAttachment = view.Facets.Attachment;
            FacetUri = view.Facets.Uri;
            FacetFavoriteOnly = view.Facets.FavoriteOnly;
            SearchText = view.Search;
            SelectedSort = view.Sort;
            SelectedTypeIndex = view.TypeIndex is >= 0 and <= 8 ? view.TypeIndex : 0;
        }
        finally { _suppressApply = false; }
        ApplyFilter();
    }

    [RelayCommand]
    private void DeleteView(string name)
    {
        if (_savedStore is null) return;
        _savedStore.Delete(name);
        var existing = SavedViews.FirstOrDefault(v => v.Name == name);
        if (existing is not null) SavedViews.Remove(existing);
    }

    partial void OnSelectedItemChanged(CipherListItem? value)
    {
        if (_isRebuildingFilteredItems && value is null)
            return;

        UpdateSelectedItemProjection(value);
    }

    private void UpdateSelectedItemProjection(CipherListItem? value)
    {
        Detail = value is null ? null : ProjectDetail(value.Id);
        OnPropertyChanged(nameof(HasDetailSelected));
        OnPropertyChanged(nameof(NoSelection));
        OnPropertyChanged(nameof(IsSelectedItemDeleted));
        OnPropertyChanged(nameof(IsSelectedItemArchived));
        OnPropertyChanged(nameof(DetailIconDomain));
        OnPropertyChanged(nameof(ShowDetailNotes));
        OnPropertyChanged(nameof(ShowReadOnlyNotice));
        OnPropertyChanged(nameof(ShowHiddenSecretsNotice));
        OnPropertyChanged(nameof(ShowProtectedPasswordCopy));
        OnPropertyChanged(nameof(DetailLocation));
    }

    // 选中条目只投影安全视图:仅当条目级门禁已授权时才携带敏感字段,否则返回脱敏投影。
    private CipherDetail ProjectDetail(string id)
    {
        var policy = _service.GetAccessPolicy(id);
        var authorized = _reprompt?.IsAuthorized(policy) == true;
        return _service.GetDetail(id, includeSensitive: authorized);
    }

    partial void OnSearchTextChanged(string value)
    {
        RefreshRecentUseSnapshot();
        if (!_suppressApply) ApplyFilter();
        OnPropertyChanged(nameof(HasActiveRefinement));
        OnPropertyChanged(nameof(NoResultsTitle));
        OnPropertyChanged(nameof(NewSearchItemLabel));
    }

    public void SelectFilterByTag(string? tag)
    {
        var nextFilter = FindFilterByTag(tag)
            ?? Filters.FirstOrDefault(f => f.Kind == FilterKind.AllItems)
            ?? Filters.FirstOrDefault();
        var nextTag = TagForFilter(nextFilter);
        var scopeChanged = nextTag != SelectedFilterTag;
        if (!scopeChanged || _suppressApply)
        {
            SelectedFilter = nextFilter;
            return;
        }

        RememberCurrentScope();
        _suppressApply = true;
        try
        {
            SelectedFilter = nextFilter;
            var context = nextTag is not null && _workspaceContexts.TryGetValue(nextTag, out var saved)
                ? saved
                : new VaultWorkspaceContext(string.Empty, new VaultFacets(false, false, false, false), VaultSortKey.RecentUse, 0, 0, null, false);
            SearchText = context.Search;
            FacetTotp = context.Facets.Totp;
            FacetAttachment = context.Facets.Attachment;
            FacetUri = context.Facets.Uri;
            FacetFavoriteOnly = context.Facets.FavoriteOnly;
            SelectedSort = context.Sort;
            SelectedTypeIndex = context.TypeIndex;
            _workspaceVerticalOffset = context.VerticalOffset;
            _workspaceFocusedItemId = context.FocusedItemId;
            _workspaceSearchHadFocus = context.SearchHadFocus;
            SelectedItem = null;
        }
        finally
        {
            _suppressApply = false;
        }
        ApplyFilter();
        OnPropertyChanged(nameof(WorkspaceContext));
    }

    private FilterNode? FindFilterByTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        return tag switch
        {
            "vault:allitems" => Filters.FirstOrDefault(f => f.Kind == FilterKind.AllItems),
            "vault:favorites" => Filters.FirstOrDefault(f => f.Kind == FilterKind.Favorites),
            "vault:totp" => Filters.FirstOrDefault(f => f.Kind == FilterKind.Totp),
            "vault:archive" => Filters.FirstOrDefault(f => f.Kind == FilterKind.Archive),
            "vault:trash" => Filters.FirstOrDefault(f => f.Kind == FilterKind.Trash),
            _ when tag.StartsWith("vault:type:", StringComparison.Ordinal) => FindTypeFilter(tag["vault:type:".Length..]),
            _ when tag.StartsWith("vault:folder:", StringComparison.Ordinal) => FindFolderFilter(tag["vault:folder:".Length..]),
            _ when tag.StartsWith("vault:organization:", StringComparison.Ordinal) =>
                Filters.FirstOrDefault(filter => filter.Kind == FilterKind.Organization
                    && filter.OrganizationId == tag["vault:organization:".Length..]),
            _ when tag.StartsWith("vault:collection:", StringComparison.Ordinal) =>
                Filters.FirstOrDefault(filter => filter.Kind == FilterKind.Collection
                    && filter.CollectionId == tag["vault:collection:".Length..]),
            _ => null,
        };
    }

    private FilterNode? FindTypeFilter(string typeName) =>
        Enum.TryParse(typeName, out VaultItemKind type)
            ? Filters.FirstOrDefault(f => f.Kind == FilterKind.Type && f.TypeFilter == type)
            : null;

    private FilterNode? FindFolderFilter(string folderId) =>
        Filters.FirstOrDefault(f => f.Kind == FilterKind.Folder && f.FolderId == folderId);

    partial void OnSelectedFilterChanged(FilterNode? oldValue, FilterNode? newValue)
    {
        if (!_isRebuildingFilteredItems && TagForFilter(oldValue) != TagForFilter(newValue))
            RefreshRecentUseSnapshot();
        if (!_suppressApply) ApplyFilter();
        OnPropertyChanged(nameof(SelectedFilterTag));
        OnPropertyChanged(nameof(IsFolderFilterSelected));
        OnPropertyChanged(nameof(IsTrashFilterSelected));
        OnPropertyChanged(nameof(IsArchiveFilterSelected));
        OnPropertyChanged(nameof(ShowArchiveSelectionAction));
        OnPropertyChanged(nameof(ShowUnarchiveSelectionAction));
        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(EmptyStateTitle));
    }

    private static string? TagForFilter(FilterNode? filter) => filter?.Kind switch
    {
        FilterKind.AllItems => "vault:allitems",
        FilterKind.Favorites => "vault:favorites",
        FilterKind.Totp => "vault:totp",
        FilterKind.Archive => "vault:archive",
        FilterKind.Trash => "vault:trash",
        FilterKind.Type when filter.TypeFilter is not null => $"vault:type:{filter.TypeFilter}",
        FilterKind.Folder when !string.IsNullOrWhiteSpace(filter.FolderId) => $"vault:folder:{filter.FolderId}",
        FilterKind.Organization when !string.IsNullOrWhiteSpace(filter.OrganizationId) =>
            $"vault:organization:{filter.OrganizationId}",
        FilterKind.Collection when !string.IsNullOrWhiteSpace(filter.CollectionId) =>
            $"vault:collection:{filter.CollectionId}",
        _ => null,
    };

    private void ApplyFilter()
    {
        var selectedId = SelectedItem?.Id;
        var wasRebuildingFilteredItems = _isRebuildingFilteredItems;
        _isRebuildingFilteredItems = true;
        try
        {
            FilteredItems.Clear();

            IEnumerable<CipherListItem> baseItems = SelectedFilter?.Kind switch
            {
                FilterKind.Favorites => Items.Where(i => i.Favorite && !i.IsDeleted && !i.IsArchived),
                FilterKind.Totp => Items.Where(i => i.HasTotp && !i.IsDeleted && !i.IsArchived),
                FilterKind.Archive   => Items.Where(i => i.IsArchived && !i.IsDeleted),
                FilterKind.Trash     => Items.Where(i => i.IsDeleted),
                FilterKind.Type      => Items.Where(i => i.Kind == SelectedFilter.TypeFilter && !i.IsDeleted && !i.IsArchived),
                FilterKind.Folder    => Items.Where(i => i.FolderId == SelectedFilter.FolderId && !i.IsDeleted && !i.IsArchived),
                FilterKind.Organization => Items.Where(i =>
                    i.OrganizationId == SelectedFilter.OrganizationId && !i.IsDeleted && !i.IsArchived),
                FilterKind.Collection => Items.Where(i =>
                    i.CollectionIds.Contains(SelectedFilter.CollectionId ?? string.Empty, StringComparer.Ordinal)
                    && !i.IsDeleted
                    && !i.IsArchived),
                _                    => Items.Where(i => !i.IsDeleted && !i.IsArchived), // AllItems 及未选中
            };

            if (SelectedTypeIndex > 0 && SelectedTypeIndex <= TypeOrder.Length)
                baseItems = baseItems.Where(item => item.Kind == TypeOrder[SelectedTypeIndex - 1]);

            foreach (var i in VaultQuery.Apply(baseItems, SearchText, CurrentFacets, SelectedSort))
                FilteredItems.Add(i);

            RebuildGroups();
        }
        finally
        {
            _isRebuildingFilteredItems = wasRebuildingFilteredItems;
        }

        if (!wasRebuildingFilteredItems)
        {
            RestoreVisibleSelection(selectedId);
            if (IsSelectionMode)
                SelectionRebindRequested?.Invoke(this, EventArgs.Empty);
        }
        OnPropertyChanged(nameof(HasNoItems));
        OnPropertyChanged(nameof(NoResults));
        OnPropertyChanged(nameof(HasActiveRefinement));
        OnPropertyChanged(nameof(TrashCount));
        OnPropertyChanged(nameof(HasTrashItems));
        OnPropertyChanged(nameof(IsTrashEmpty));
        OnPropertyChanged(nameof(CanEmptyRecycleBin));
        OnPropertyChanged(nameof(TrashHeader));
        OnPropertyChanged(nameof(ArchiveCount));
        OnPropertyChanged(nameof(IsArchiveEmpty));
        OnPropertyChanged(nameof(VisibleItemCountText));
    }

    private void RestoreVisibleSelection(string? id)
    {
        var item = string.IsNullOrEmpty(id)
            ? null
            : FilteredItems.FirstOrDefault(item => item.Id == id);
        if (item is null && !string.IsNullOrEmpty(id) && AutoSelectFirstResult && !IsEditing)
            item = FilteredItems.FirstOrDefault();

        if (SelectedItem is null && item is null)
        {
            if (Detail is not null)
                UpdateSelectedItemProjection(null);
            return;
        }

        SelectedItem = item;
    }

    private static readonly VaultItemKind[] TypeOrder =
    {
        VaultItemKind.Login,
        VaultItemKind.Card,
        VaultItemKind.Identity,
        VaultItemKind.Note,
        VaultItemKind.Ssh,
        VaultItemKind.BankAccount,
        VaultItemKind.DriversLicense,
        VaultItemKind.Passport,
    };

    private static string TypeDisplayName(VaultItemKind kind) => kind switch
    {
        VaultItemKind.Login => "登录",
        VaultItemKind.Card => "银行卡",
        VaultItemKind.Identity => "身份",
        VaultItemKind.Note => "笔记",
        VaultItemKind.Ssh => "SSH 密钥",
        VaultItemKind.BankAccount => "银行账户",
        VaultItemKind.DriversLicense => "驾驶证",
        VaultItemKind.Passport => "护照",
        _ => "其他",
    };

    private string FolderNameFor(string? folderId)
    {
        if (string.IsNullOrEmpty(folderId))
            return "无文件夹";
        var folder = Filters.FirstOrDefault(f => f.Kind == FilterKind.Folder && f.FolderId == folderId);
        return folder?.Label ?? "无文件夹";
    }

    private void RebuildGroups()
    {
        GroupedItems.Clear();
        foreach (var item in FilteredItems)
            item.GroupHeader = string.Empty;
        if (FilteredItems.Count == 0)
            return;

        if (IsSelectionMode || SelectedSort != VaultSortKey.RecentUse || !string.IsNullOrWhiteSpace(SearchText))
        {
            var flat = new VaultListGroup { Key = string.Empty, ShowHeader = false };
            foreach (var i in FilteredItems) flat.Add(i);
            GroupedItems.Add(flat);
            return;
        }

        foreach (var groupItems in FilteredItems.GroupBy(item =>
            item.LastUsedAt?.LocalDateTime.Date >= _recentGroupDate ? "今天"
                : item.LastUsedAt?.LocalDateTime.Date >= _recentGroupDate.AddDays(-6) ? "过去 7 天"
                : "更早"))
        {
            var group = new VaultListGroup { Key = groupItems.Key, ShowHeader = true };
            foreach (var item in groupItems) group.Add(item);
            if (group.FirstOrDefault() is { } first)
                first.GroupHeader = group.Key;
            GroupedItems.Add(group);
        }
    }

    private void RefreshRecentUseSnapshot()
    {
        _recentGroupDate = DateTime.Today;
        foreach (var item in Items)
        {
            var usedAt = _recentUses.TryGetValue(item.Id, out var latest)
                ? latest : _recentUseStore?.GetLastUsed(item.Id);
            item.LastUsedAt = usedAt;
            _recentOrderSnapshot[item.Id] = usedAt;
        }
    }

    public void RecordRecentUse(string id)
    {
        if (Items.All(item => item.Id != id))
            return;
        var usedAt = DateTimeOffset.UtcNow;
        _recentUses[id] = usedAt;
        _recentUseStore?.Record(id, usedAt);
        // Keep the visible list stable until navigation or an explicit sort change.
    }

    public DateTimeOffset? GetLastUsedAt(string id) =>
        _recentUses.TryGetValue(id, out var usedAt) ? usedAt : _recentUseStore?.GetLastUsed(id);

    public void BeginAdd(VaultItemKind type)
    {
        LoadPendingCipherCreateFromLedger();
        if (!CanBeginCipherCreate)
        {
            OperationError = _pendingCipherCreate is not null
                ? "请先同步并确认上一次新建结果，再创建其他条目。"
                : _service.HasPendingOrInFlightCipherCreate()
                    ? "另一个新建操作仍在进行或等待确认。"
                    : "当前有保险库操作正在进行。";
            return;
        }

        if (IsNewCipherType(type) && !CanCreateNewCipherTypes)
        {
            OperationError = "当前服务器未确认支持创建此类型的条目。";
            return;
        }

        _editingId = null;
        OnPropertyChanged(nameof(CanChangeEditorType));
        EditorDraft = CipherEditorDraft.CreateDefault(type);
        if (SelectedFilter is { Kind: FilterKind.Folder, FolderId: { } folderId })
            EditorDraft.FolderId = folderId;
        ConfigureEditorDestinations(canChoose: true);
        ResetEditorBaseline();
        EditorError = EditorDestinations.Count == 0
            ? "当前账户没有可写入的密码库或组织集合。"
            : string.Empty;
        OperationError = string.Empty;
        IsEditing = true;
        SelectedItem = null;
        Detail = null;
        OnPropertyChanged(nameof(HasDetailSelected));
        OnPropertyChanged(nameof(NoSelection));
        OnPropertyChanged(nameof(DetailIconDomain));
        OnPropertyChanged(nameof(EditorTitle));
    }

    // 统一条目级授权:Reprompt==false 直接通过;缺失门禁服务 fail-closed;否则走门禁验证。
    // 授权结果在 await 后由门禁自身用 lease 校验会话未变;调用方仅在成功后才读取/绑定秘密。
    private async Task<UiOperationResult<bool>> EnsureAuthorizedAsync(
        string id,
        SensitiveItemAction action,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct)
    {
        var policy = _service.GetAccessPolicy(id);
        if (!policy.RequiresReprompt)
            return UiOperationResult<bool>.Success(true);
        if (_reprompt is null)
            return UiOperationResult<bool>.Failure("敏感条目验证服务不可用");
        return await _reprompt.AuthorizeAsync(policy, action, prompt, ct);
    }

    // 编辑必须先过门禁再读 draft:未授权时绝不构造/暴露可编辑的明文草稿。
    public async Task<UiOperationResult> BeginEditAsync(
        string id,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default)
    {
        var lifecycleEpoch = _lifecycleEpoch;
        LoadPendingCipherCreateFromLedger();
        if (!CanStartVaultOperation)
            return UiOperationResult.Busy();
        if (_pendingCipherCreate is not null)
            return UiOperationResult.Failure("请先同步并确认上一次新建结果。");

        var source = Items.FirstOrDefault(item => item.Id == id);
        if (source is { CanViewPassword: false })
            return UiOperationResult.Failure("该组织条目已隐藏密码，不能编辑。请联系组织管理员调整权限。");
        if (source?.CanModify != true)
            return UiOperationResult.Failure("当前条目不能编辑。");

        var authorization = await EnsureAuthorizedAsync(
            id, SensitiveItemAction.Edit, prompt, ct);
        if (!authorization.IsSuccess)
            return new UiOperationResult(authorization.Status, authorization.Error);
        if (lifecycleEpoch != _lifecycleEpoch)
            return UiOperationResult.Cancelled();
        if (!CanStartVaultOperation)
            return UiOperationResult.Busy();
        if (_pendingCipherCreate is not null)
            return UiOperationResult.Failure("请先同步并确认上一次新建结果。");

        CipherEditorDraft draft;
        try
        {
            draft = _service.GetDraft(id);
        }
        catch (Core.Services.OrganizationCipherOperationException)
        {
            return UiOperationResult.Failure("该组织条目的密码查看权限已被撤销，不能编辑。");
        }
        catch (InvalidOperationException)
        {
            return UiOperationResult.Failure("条目已发生变化，请刷新后重试。");
        }

        EditorDraft = draft;
        _editingId = id;
        OnPropertyChanged(nameof(CanChangeEditorType));
        ConfigureEditorDestinations(canChoose: false);
        ResetEditorBaseline();
        EditorError = string.Empty;
        OperationError = string.Empty;
        IsEditing = true;
        OnPropertyChanged(nameof(EditorTitle));
        return UiOperationResult.Success();
    }

    public async Task<UiOperationResult> BeginCloneAsync(
        string id,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default)
    {
        var lifecycleEpoch = _lifecycleEpoch;
        LoadPendingCipherCreateFromLedger();
        if (!CanStartVaultOperation)
            return UiOperationResult.Busy();
        if (!CanBeginCipherCreate)
            return UiOperationResult.Failure("请先同步并确认上一次新建结果。");

        var source = Items.FirstOrDefault(item => item.Id == id);
        if (source is { CanViewPassword: false })
            return UiOperationResult.Failure("该组织条目已隐藏密码，不能创建副本。请联系组织管理员调整权限。");
        if (source?.CanClone != true)
            return UiOperationResult.Failure("当前条目不能创建副本。");

        var authorization = await EnsureAuthorizedAsync(
            id, SensitiveItemAction.Clone, prompt, ct);
        if (!authorization.IsSuccess)
            return new UiOperationResult(authorization.Status, authorization.Error);
        if (lifecycleEpoch != _lifecycleEpoch)
            return UiOperationResult.Cancelled();
        if (!CanStartVaultOperation)
            return UiOperationResult.Busy();
        if (!CanBeginCipherCreate)
            return UiOperationResult.Failure("请先同步并确认上一次新建结果。");

        CipherEditorDraft draft;
        try
        {
            draft = _service.GetDraft(id);
        }
        catch (Core.Services.OrganizationCipherOperationException)
        {
            return UiOperationResult.Failure("该组织条目的密码查看权限已被撤销，不能创建副本。");
        }
        catch (InvalidOperationException)
        {
            return UiOperationResult.Failure("条目已发生变化，请刷新后重试。");
        }

        draft.Name = string.IsNullOrWhiteSpace(draft.Name)
            ? "未命名条目 副本"
            : $"{draft.Name} 副本";
        draft.Favorite = false;
        EditorDraft = draft;
        _editingId = null;
        OnPropertyChanged(nameof(CanChangeEditorType));
        ConfigureEditorDestinations(canChoose: true);
        ResetEditorBaseline();
        EditorError = string.Empty;
        OperationError = string.Empty;
        IsSelectionMode = false;
        IsEditing = true;
        SelectedItem = null;
        Detail = null;
        OnPropertyChanged(nameof(HasDetailSelected));
        OnPropertyChanged(nameof(NoSelection));
        OnPropertyChanged(nameof(DetailIconDomain));
        OnPropertyChanged(nameof(EditorTitle));
        return UiOperationResult.Success();
    }

    public void CancelEdit()
    {
        if (!CanCancelEditor)
            return;

        CloseEditor();
    }

    private void CloseEditor()
    {
        IsEditing = false;
        EditorDraft = null;
        _ownedPendingCipherCreateFingerprint = null;
        EditorError = string.Empty;
        OperationError = string.Empty;
        _editingId = null;
        OnPropertyChanged(nameof(CanChangeEditorType));
        _canChooseEditorDestination = false;
        EditorDestinations = new ObservableCollection<CipherDestinationOption> { CipherDestinationOption.Personal };
        EditorCollectionOptions = new ObservableCollection<CipherCollectionOption>();
        OnPropertyChanged(nameof(CanChooseEditorDestination));
        OnPropertyChanged(nameof(ShowEditorCollectionOptions));
        OnPropertyChanged(nameof(EditorTitle));
    }

    public void ChangeEditorType(VaultItemKind type)
    {
        if (EditorDraft is null || !CanChangeEditorType || EditorDraft.Type == type)
            return;
        if (IsNewCipherType(type) && !CanCreateNewCipherTypes)
        {
            EditorError = "当前服务器未确认支持创建此类型的条目。";
            return;
        }

        EditorDraft.Type = type;
        EditorError = string.Empty;
        OnPropertyChanged(nameof(EditorTitle));
    }

    private static bool IsNewCipherType(VaultItemKind type) => type is
        VaultItemKind.BankAccount
        or VaultItemKind.DriversLicense
        or VaultItemKind.Passport;

    private void ConfigureEditorDestinations(bool canChoose)
    {
        _canChooseEditorDestination = canChoose;
        var destinations = _service.GetCipherDestinations().ToList();
        if (!canChoose
            && EditorDraft is { } draft
            && !destinations.Any(destination =>
                string.Equals(destination.OrganizationId, draft.OrganizationId, StringComparison.Ordinal)))
        {
            destinations.Add(new CipherDestinationOption(
                draft.OrganizationId is null ? "个人密码库（当前）" : "当前组织位置",
                draft.OrganizationId,
                null));
        }

        EditorDestinations = new ObservableCollection<CipherDestinationOption>(destinations);
        if (canChoose)
        {
            if (EditorSelectedDestination is null && destinations.FirstOrDefault() is { } first)
                EditorSelectedDestination = first;
            else if (EditorDraft?.OrganizationId is { } organizationId)
            {
                EditorCollectionOptions = new ObservableCollection<CipherCollectionOption>(BuildWritableNewCipherCollectionOptions(
                    organizationId,
                    EditorDraft.CollectionIds));
                EditorDraft.ReplaceCollectionIds(
                    EditorCollectionOptions
                        .Where(option => option.IsSelected)
                        .Select(option => option.Id));
            }
        }
        else
        {
            EditorCollectionOptions = new ObservableCollection<CipherCollectionOption>();
        }
        OnPropertyChanged(nameof(CanChooseEditorDestination));
        OnPropertyChanged(nameof(EditorSelectedDestination));
        OnPropertyChanged(nameof(ShowEditorCollectionOptions));
    }

    private IReadOnlyList<CipherCollectionOption> BuildWritableNewCipherCollectionOptions(
        string organizationId,
        IReadOnlyCollection<string> selectedCollectionIds)
    {
        var selected = selectedCollectionIds.ToHashSet(StringComparer.Ordinal);
        var options = _service.GetCipherCollectionOptions(organizationId);
        foreach (var option in options)
            option.IsSelected = selected.Contains(option.Id);
        return options;
    }

    // 唯一 Vault 操作 runner:_operationGate 原子去重(第二个并发调用立即返回 Busy,不触发 action);
    // 取消经 _operationCts 与调用方 ct 联动;已知业务异常映射为安全文案,未知异常不捕获(交由 Task 12)。
    private async Task<UiOperationResult<T>> RunOperationAsync<T>(
        VaultOperationKind kind,
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default,
        Func<T>? committedRefreshFailure = null)
    {
        if (!_operationGate.TryEnter(out var operation))
            return UiOperationResult<T>.Busy();

        using (operation)
        {
            _operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (kind != VaultOperationKind.ResolveCipherCreate)
                SetPendingCreateAbandonedWarning(false);
            CurrentOperation = kind;
            OperationError = string.Empty;
            try
            {
                return UiOperationResult<T>.Success(await action(_operationCts.Token));
            }
            catch (OperationCanceledException ex)
                when (UiExceptionPolicy.IsGenuineCancellation(ex, _operationCts.Token))
            {
                return UiOperationResult<T>.Cancelled();
            }
            catch (Core.Services.VaultRefreshException) when (committedRefreshFailure is not null)
            {
                if (kind == VaultOperationKind.SaveCipher && !CanPublishCurrentCipherSave())
                    return UiOperationResult<T>.Cancelled();
                DataStale = true;
                OperationWarning = "更改已保存，但本地密码库刷新失败，请手动同步。";
                ReloadItems();
                ReloadFilters();
                return UiOperationResult<T>.Success(committedRefreshFailure());
            }
            catch (Core.Services.CipherCreateOutcomeUncertainException ex)
            {
                var pending = _service.RememberPendingCipherCreate(ex.Pending);
                if (!CanPublishPendingCipherCreate(pending))
                    return UiOperationResult<T>.Cancelled();
                ClaimPendingCipherCreateOwnership(pending);
                BlockPendingCipherCreate(
                    pending,
                    "无法确认新条目是否已保存。请手动同步，在确认结果前不要重复提交。",
                    "新条目的保存结果不确定，已保留当前草稿。");
                return UiOperationResult<T>.Failure(OperationError);
            }
            catch (Core.Services.CipherCreateSnapshotMismatchException ex)
            {
                var pending = _service.RememberPendingCipherCreate(ex.Pending);
                if (!CanPublishPendingCipherCreate(pending))
                    return UiOperationResult<T>.Cancelled();
                ClaimPendingCipherCreateOwnership(pending);
                ReloadItems();
                ReloadFilters();
                BlockPendingCipherCreate(
                    pending,
                    "新条目可能已保存，但本地快照无法可靠核对。请手动同步。",
                    "无法确认新条目的服务器记录，已保留当前草稿。");
                return UiOperationResult<T>.Failure(OperationError);
            }
            catch (Core.Services.VaultBatchMutationRecoveryException)
            {
                DataStale = true;
                OperationWarning = "操作可能已部分完成，且无法刷新本地密码库。请立即手动同步。";
                OperationError = "无法确认服务器更改是否完整完成。";
                return UiOperationResult<T>.Failure(OperationError);
            }
            catch (Core.Services.VaultMutationReconciliationException)
            {
                OperationWarning = "操作仅部分完成，已刷新为服务器当前状态。";
                OperationError = "服务器未对所有所选条目应用更改。";
                return UiOperationResult<T>.Failure(OperationError);
            }
            catch (Exception ex) when (
                ex is Core.Services.AttachmentTooLargeException
                    or Core.Services.CipherEditConflictException
                    or Core.Services.LegacyCipherAttachmentTransferException
                    or Core.Services.InvalidCipherWriteShapeException
                    or Core.Services.UnsupportedCipherTypeWriteException
                    or Core.Services.UnsupportedCipherArchiveOperationException
                    or Core.Services.OrganizationCipherOperationException
                    or Core.Services.OrganizationPolicyException
                    or Core.Services.CipherCreatePendingOperationException
                    or Vault.AttachmentUploadCompensationException
                    or Api.VaultWriteException
                    or HttpRequestException
                    or IOException
                    or InvalidDataException
                    or UnauthorizedAccessException
                    or System.Security.Cryptography.CryptographicException
                    or TimeoutException
                || Api.RequestTimeoutClassifier.IsTimeout(ex, _operationCts.Token))
            {
                OperationError = kind == VaultOperationKind.ResolveCipherCreate
                    ? "无法清除本机待确认状态，防重复保护仍然有效。请检查本地存储后重试。"
                    : SafeOperationMessage(ex);
                return UiOperationResult<T>.Failure(OperationError);
            }
            finally
            {
                _operationCts.Dispose();
                _operationCts = null;
                CurrentOperation = null;
            }
        }
    }

    private void BlockPendingCipherCreate(
        CipherCreatePending pending,
        string warning,
        string error)
    {
        SetPendingCreateAbandonedWarning(false);
        _pendingCipherCreate = pending;
        DataStale = true;
        OperationWarning = warning;
        OperationError = error;
        NotifyPendingCipherCreateChanged();
    }

    private bool LoadPendingCipherCreateFromLedger()
    {
        if (_service.GetPendingCipherCreate() is not { } pending)
            return _pendingCipherCreate is not null;
        if (_pendingCipherCreate is not null
            && PendingCreateStateMatches(_pendingCipherCreate, pending))
        {
            return true;
        }

        BlockPendingCipherCreate(
            pending,
            "新条目的保存结果仍待确认。请先同步，不要重复提交。",
            "检测到此账户存在待确认的新建操作。");
        return true;
    }

    private void ClearPendingCipherCreate()
    {
        if (_pendingCipherCreate is null)
            return;

        _pendingCipherCreate = null;
        NotifyPendingCipherCreateChanged();
    }

    private void NotifyPendingCipherCreateChanged()
    {
        OnPropertyChanged(nameof(HasPendingCipherCreate));
        OnPropertyChanged(nameof(CanSaveEditorDraft));
        OnPropertyChanged(nameof(CanCancelEditor));
        OnPropertyChanged(nameof(CanBeginCipherCreate));
        OnPropertyChanged(nameof(CanEditEditorDraft));
        OnPropertyChanged(nameof(CanChangeEditorType));
        OnPropertyChanged(nameof(CanCheckPendingCipherCreate));
        OnPropertyChanged(nameof(CanAbandonPendingCipherCreate));
        OnPropertyChanged(nameof(HasRecoverablePendingDraft));
        OnPropertyChanged(nameof(PendingCipherCreateTitle));
        OnPropertyChanged(nameof(PendingCipherCreateMessage));
        OnPropertyChanged(nameof(PendingCipherCreateDraftMessage));
        OnPropertyChanged(nameof(ShowOperationWarningInfo));
        OnPropertyChanged(nameof(OperationWarningTitle));
    }

    private void SetPendingCreateAbandonedWarning(bool value)
    {
        if (_showPendingCreateAbandonedWarning == value)
            return;

        _showPendingCreateAbandonedWarning = value;
        OnPropertyChanged(nameof(OperationWarningTitle));
    }

    private void SubscribeToCipherCreateGateChanges(IVaultUiService service)
    {
        var weakTarget = new WeakReference<VaultViewModel>(this);
        var synchronizationContext = SynchronizationContext.Current;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!weakTarget.TryGetTarget(out _))
            {
                service.CipherCreateGateChanged -= handler!;
                return;
            }

            if (synchronizationContext is null
                || ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
            {
                if (weakTarget.TryGetTarget(out var target))
                    target.OnCipherCreateGateChanged();
                return;
            }

            synchronizationContext.Post(
                static state =>
                {
                    var weak = (WeakReference<VaultViewModel>)state!;
                    if (weak.TryGetTarget(out var target))
                        target.OnCipherCreateGateChanged();
                },
                weakTarget);
        };
        service.CipherCreateGateChanged += handler;
    }

    private void OnCipherCreateGateChanged()
    {
        if (!_acceptCipherCreateGateNotifications)
            return;

        if (_service.GetPendingCipherCreate() is { } pending)
        {
            if (_pendingCipherCreate is null
                || !PendingCreateStateMatches(_pendingCipherCreate, pending))
            {
                BlockPendingCipherCreate(
                    pending,
                    "新条目的保存结果仍待确认。请先同步，不要重复提交。",
                    "检测到此账户存在待确认的新建操作。");
            }
        }
        else if (_pendingCipherCreate is not null
            && !_service.HasPendingOrInFlightCipherCreate())
        {
            var cleared = _pendingCipherCreate;
            var ownsDraft = OwnsPendingCipherCreate(cleared);
            var explicitlyAbandoned = _explicitlyAbandonedPendingCipherCreate is not null
                && PendingCreateStateMatches(cleared, _explicitlyAbandonedPendingCipherCreate);
            ClearPendingCipherCreate();
            if (explicitlyAbandoned)
            {
                _ownedPendingCipherCreateFingerprint = null;
                PublishPendingCipherCreateAbandoned();
            }
            else if (ownsDraft)
            {
                CloseEditor();
                OperationError = string.Empty;
                OperationWarning = "待确认的新条目已完成核销，请刷新密码库查看。";
            }
        }

        OnPropertyChanged(nameof(CanSaveEditorDraft));
        OnPropertyChanged(nameof(CanBeginCipherCreate));
        OnPropertyChanged(nameof(CanEditEditorDraft));
        OnPropertyChanged(nameof(CanChangeEditorType));
    }

    private void ClaimPendingCipherCreateOwnership(CipherCreatePending pending)
    {
        var context = _activeCipherSave;
        if (context is null
            || !context.IsCreate
            || context.LifecycleEpoch != _lifecycleEpoch
            || !ReferenceEquals(EditorDraft, context.Draft)
            || !IsEditing)
        {
            return;
        }

        _ownedPendingCipherCreateFingerprint = pending.Witness.KeyFingerprint;
    }

    private bool OwnsPendingCipherCreate(CipherCreatePending pending) =>
        string.Equals(
            _ownedPendingCipherCreateFingerprint,
            pending.Witness.KeyFingerprint,
            StringComparison.Ordinal);

    private bool CanPublishPendingCipherCreate(CipherCreatePending pending)
    {
        var current = _service.GetPendingCipherCreate();
        return CanPublishCurrentCipherSave()
            && current is not null
            && string.Equals(
                current.Witness.AccountId,
                pending.Witness.AccountId,
                StringComparison.Ordinal)
            && current.Witness.Endpoints == pending.Witness.Endpoints
            && string.Equals(
                current.Witness.KeyFingerprint,
                pending.Witness.KeyFingerprint,
                StringComparison.Ordinal);
    }

    private bool CanPublishCurrentCipherSave()
    {
        var context = _activeCipherSave;
        return context is not null
            && context.LifecycleEpoch == _lifecycleEpoch
            && ReferenceEquals(EditorDraft, context.Draft)
            && IsEditing;
    }

    public CipherCreatePending? CapturePendingCipherCreate()
    {
        LoadPendingCipherCreateFromLedger();
        return _pendingCipherCreate;
    }

    public async Task<UiOperationResult> AbandonPendingCipherCreateAsync(
        CipherCreatePending expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(expected.Witness);
        LoadPendingCipherCreateFromLedger();
        if (_pendingCipherCreate is null
            || !PendingCreateStateMatches(_pendingCipherCreate, expected))
        {
            OperationError = "待确认状态已变化，请重新检查后再决定是否放弃。";
            return UiOperationResult.Failure(OperationError);
        }

        var lifecycleEpoch = _lifecycleEpoch;
        _explicitlyAbandonedPendingCipherCreate = expected;
        try
        {
            var result = await RunOperationAsync(
                VaultOperationKind.ResolveCipherCreate,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (lifecycleEpoch != _lifecycleEpoch)
                        throw new OperationCanceledException(token);
                    return Task.FromResult(_service.AbandonPendingCipherCreate(expected));
                },
                ct);
            if (!result.IsSuccess)
                return new UiOperationResult(result.Status, result.Error);
            if (lifecycleEpoch != _lifecycleEpoch)
                return UiOperationResult.Cancelled();
            if (result.Value != true)
            {
                _explicitlyAbandonedPendingCipherCreate = null;
                OnCipherCreateGateChanged();
                if (_pendingCipherCreate is null)
                    return UiOperationResult.Success();

                OperationError = "待确认状态已变化，请重新检查后再决定是否放弃。";
                return UiOperationResult.Failure(OperationError);
            }

            if (_pendingCipherCreate is not null
                && PendingCreateStateMatches(_pendingCipherCreate, expected))
            {
                _ownedPendingCipherCreateFingerprint = null;
                ClearPendingCipherCreate();
            }
            PublishPendingCipherCreateAbandoned();
            return UiOperationResult.Success();
        }
        finally
        {
            _explicitlyAbandonedPendingCipherCreate = null;
        }
    }

    private void PublishPendingCipherCreateAbandoned()
    {
        SetPendingCreateAbandonedWarning(true);
        DataStale = true;
        OperationError = string.Empty;
        OperationWarning = "已清除本机待确认状态。服务器上仍可能存在该条目，请先查找再决定是否手动保存。";
        NotifyPendingCipherCreateChanged();
    }

    private static bool PendingCreateStateMatches(
        CipherCreatePending left,
        CipherCreatePending right) =>
        string.Equals(
            left.Witness.AccountId,
            right.Witness.AccountId,
            StringComparison.Ordinal)
        && left.Witness.Endpoints == right.Witness.Endpoints
        && string.Equals(
            left.Witness.KeyFingerprint,
            right.Witness.KeyFingerprint,
            StringComparison.Ordinal)
        && left.CommitConfirmed == right.CommitConfirmed
        && string.Equals(
            left.ExpectedCipherId,
            right.ExpectedCipherId,
            StringComparison.Ordinal);

    private static string SafeOperationMessage(Exception ex)
    {
        if (ex is Vault.AttachmentUploadCompensationException compensation)
        {
            return $"附件上传未完成，且服务器记录 {compensation.OrphanedAttachmentId} 可能需要手动清理。";
        }
        if (Api.RequestTimeoutClassifier.IsTimeout(ex))
            return "文件传输长时间没有进展，请检查连接后重试。";

        return ex switch
        {
            Core.Services.AttachmentTooLargeException => ex.Message,
            Core.Services.CipherEditConflictException =>
                "此条目已在其他位置更新或删除，请重新加载后再编辑。",
            Core.Services.LegacyCipherAttachmentTransferException =>
                "此旧格式条目包含附件，当前无法安全转移到组织。",
            Core.Services.VaultMutationReconciliationException =>
                "服务器未对所有条目应用更改，已刷新为服务器当前状态。",
            Core.Services.InvalidCipherWriteShapeException => ex.Message,
            Core.Services.UnsupportedCipherTypeWriteException => ex.Message,
            Core.Services.UnsupportedCipherArchiveOperationException => ex.Message,
            Core.Services.OrganizationCipherOperationException =>
                "当前账户没有此组织条目的写入权限，或组织密钥不可用。",
            Core.Services.OrganizationPolicyException =>
                "组织策略不允许执行此操作。",
            Core.Services.CipherCreatePendingOperationException =>
                "另一个新建操作仍在进行或等待确认，请先同步。",
            Api.VaultWriteException => "服务器拒绝了此更改，请检查输入后重试。",
            InvalidDataException or System.Security.Cryptography.CryptographicException =>
                "附件内容无效或完整性校验失败。",
            UnauthorizedAccessException => "没有访问所选文件的权限。",
            IOException => "文件读取或写入失败，请检查磁盘和文件状态后重试。",
            HttpRequestException => "网络请求失败，请检查连接后重试。",
            _ => "操作失败。",
        };
    }

    /// <summary>取消当前正在进行的 Vault 操作(若有)。用于生命周期清理与用户主动取消,不影响后续新操作进入门禁。</summary>
    public void CancelCurrentOperation() => _operationCts?.Cancel();

    public async Task<UiOperationResult<string>> SaveDraftAsync(CancellationToken ct = default)
    {
        if (LoadPendingCipherCreateFromLedger()
            && (_editingId is null || HasOwnedPendingCipherCreate))
        {
            EditorError = "请先手动同步并确认上一次保存结果。";
            return UiOperationResult<string>.Failure(EditorError);
        }

        if (_editingId is null && _service.HasPendingOrInFlightCipherCreate())
        {
            EditorError = "另一个新建操作仍在进行或等待确认，请先同步。";
            return UiOperationResult<string>.Failure(EditorError);
        }

        if (EditorDraft is null)
            return UiOperationResult<string>.Failure("没有可保存的草稿");
        if (_editingId is null && EditorSelectedDestination is null)
        {
            EditorError = "当前账户没有可写入的密码库或组织集合。";
            return UiOperationResult<string>.Failure(EditorError);
        }
        if (_canChooseEditorDestination)
        {
            EditorDraft.ReplaceCollectionIds(
                EditorCollectionOptions
                    .Where(option => option.IsSelected)
                    .Select(option => option.Id));
        }
        var errors = EditorDraft.Validate();
        if (errors.Count > 0)
        {
            EditorError = string.Join(Environment.NewLine, errors);
            return UiOperationResult<string>.Failure(EditorError);
        }

        var draft = EditorDraft;
        var editingId = _editingId;
        var saveContext = new CipherSaveUiContext(
            _lifecycleEpoch,
            draft,
            IsCreate: editingId is null);
        if (Interlocked.CompareExchange(ref _activeCipherSave, saveContext, null) is not null)
            return UiOperationResult<string>.Busy();
        try
        {
            return await RunOperationAsync(
                VaultOperationKind.SaveCipher,
                async token =>
                {
                    var savedId = await _service.SaveCipherAsync(draft, editingId, token);
                    if (!CanPublishCurrentCipherSave())
                        throw new OperationCanceledException(token);
                    ReloadItems();
                    ReloadFilters();
                    SkippedCipherCount = _service.SkippedCipherCount;
                    UnsupportedOrganizationCipherCount = _service.UnsupportedOrganizationCipherCount;
                    var createdCipherIsVisible = editingId is not null
                        || Items.Any(item => string.Equals(item.Id, savedId, StringComparison.Ordinal));
                    if (createdCipherIsVisible)
                    {
                        if (editingId is null)
                            SelectCreatedCipher(savedId);
                        else
                            SelectById(savedId);
                    }
                    else
                    {
                        SelectedItem = null;
                        OperationWarning = "新条目已确认保存，但当前无法解密或不可见。请勿重新创建。";
                    }
                    CloseEditor();
                    return savedId;
                },
                ct,
                committedRefreshFailure: () =>
                {
                    CloseEditor();
                    return editingId ?? string.Empty;
                });
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeCipherSave, null, saveContext);
        }
    }

    private async Task<UiOperationResult> RunWriteAndReloadAsync(
        VaultOperationKind kind,
        Func<CancellationToken, Task> operation,
        string? selectId,
        CancellationToken ct)
    {
        var result = await RunOperationAsync(
            kind,
            async token =>
            {
                await operation(token);
                var previouslySelectedId = SelectedItem?.Id;
                ReloadItems();
                ReloadFilters();
                if (selectId is null)
                    RestoreVisibleSelection(previouslySelectedId);
                else
                    SelectById(selectId);
                return true;
            },
            ct,
            committedRefreshFailure: () => true);
        return new UiOperationResult(result.Status, result.Error);
    }

    public Task<UiOperationResult> SoftDeleteAsync(string id, CancellationToken ct = default) =>
        RunWriteAndReloadAsync(
            VaultOperationKind.DeleteCipher,
            token => _service.DeleteCipherAsync(id, permanent: false, token),
            selectId: null,
            ct);

    public Task<UiOperationResult> PermanentDeleteAsync(string id, CancellationToken ct = default) =>
        RunWriteAndReloadAsync(
            VaultOperationKind.DeleteCipher,
            token => _service.DeleteCipherAsync(id, permanent: true, token),
            selectId: null,
            ct);

    public Task<UiOperationResult> RestoreAsync(string id, CancellationToken ct = default) =>
        RunWriteAndReloadAsync(
            VaultOperationKind.DeleteCipher,
            token => _service.RestoreCipherAsync(id, token),
            selectId: id,
            ct);

    public Task<UiOperationResult> ArchiveAsync(string id, CancellationToken ct = default)
    {
        if (!CanArchiveItems
            || Items.FirstOrDefault(item => item.Id == id) is not
                { CanArchive: true, IsDeleted: false, IsArchived: false })
        {
            return Task.FromResult(
                UiOperationResult.Failure("当前服务器或账户不支持此操作。"));
        }

        return RunWriteAndReloadAsync(
            VaultOperationKind.ArchiveCipher,
            token => _service.ArchiveCipherAsync(id, token),
            selectId: null,
            ct);
    }

    public Task<UiOperationResult> UnarchiveAsync(string id, CancellationToken ct = default)
    {
        if (!CanUnarchiveItems
            || Items.FirstOrDefault(item => item.Id == id) is not
                { CanUnarchive: true, IsDeleted: false, IsArchived: true })
        {
            return Task.FromResult(
                UiOperationResult.Failure("当前服务器或账户不支持此操作。"));
        }

        return RunWriteAndReloadAsync(
            VaultOperationKind.ArchiveCipher,
            token => _service.UnarchiveCipherAsync(id, token),
            selectId: null,
            ct);
    }

    public async Task<UiOperationResult> SaveFolderAsync(string? folderId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return UiOperationResult.Failure("文件夹名称不能为空。");
        var result = await RunWriteAndReloadAsync(
            VaultOperationKind.Folder,
            token => _service.SaveFolderAsync(folderId, name.Trim(), token),
            SelectedItem?.Id,
            ct);
        if (result.IsSuccess)
            FoldersChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public async Task<UiOperationResult> DeleteFolderAsync(string folderId, CancellationToken ct = default)
    {
        var result = await RunWriteAndReloadAsync(
            VaultOperationKind.Folder,
            token => _service.DeleteFolderAsync(folderId, token),
            SelectedItem?.Id,
            ct);
        if (result.IsSuccess)
            FoldersChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public CipherPlacementBaseline GetCipherPlacementBaseline(string id) =>
        _service.GetCipherPlacementBaseline(id);

    public IReadOnlyList<CipherDestinationOption> GetOrganizationDestinations() =>
        _service.GetCipherDestinations()
            .Where(destination => destination.OrganizationId is not null)
            .ToArray();

    public IReadOnlyList<CipherCollectionOption> GetCipherCollectionOptions(
        string organizationId,
        IReadOnlyCollection<string>? selectedCollectionIds = null,
        bool lockReadOnlyAssignments = false) =>
        _service.GetCipherCollectionOptions(
            organizationId,
            selectedCollectionIds,
            lockReadOnlyAssignments);

    public Task<UiOperationResult> TransferCipherToOrganizationAsync(
        CipherPlacementBaseline baseline,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default) =>
        RunPlacementOperationAsync(
            token => _service.TransferCipherToOrganizationAsync(
                baseline,
                organizationId,
                collectionIds,
                token),
            baseline.CipherId,
            ct);

    public Task<UiOperationResult> SetCipherCollectionsAsync(
        CipherPlacementBaseline baseline,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default) =>
        RunPlacementOperationAsync(
            token => _service.SetCipherCollectionsAsync(baseline, collectionIds, token),
            baseline.CipherId,
            ct);

    public async Task<UiOperationResult> TransferSelectedCiphersToOrganizationAsync(
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default)
    {
        if (!CanTransferSelectionToOrganization)
            return RejectSelectionPlacement("所选条目不能批量转移到组织。");
        if (string.IsNullOrWhiteSpace(organizationId) || collectionIds.Count == 0)
            return RejectSelectionPlacement("请选择目标组织和至少一个可写集合。");

        if (!TryCaptureSelectedPlacementBaselines(out var baselines))
            return UiOperationResult.Failure(OperationError);
        var result = await RunBatchAsync(
            (_, token) => _service.TransferCiphersToOrganizationAsync(
                baselines,
                organizationId,
                collectionIds,
                token),
            ct);
        if (result.IsSuccess)
            IsSelectionMode = false;
        return result;
    }

    public Task<UiOperationResult> TransferSelectedToOrganizationAsync(
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default) =>
        TransferSelectedCiphersToOrganizationAsync(organizationId, collectionIds, ct);

    public Task<UiOperationResult> AddSelectedCiphersToCollectionsAsync(
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default) =>
        UpdateSelectedCipherCollectionsAsync(collectionIds, removeCollections: false, ct);

    public Task<UiOperationResult> RemoveSelectedCiphersFromCollectionsAsync(
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default) =>
        UpdateSelectedCipherCollectionsAsync(collectionIds, removeCollections: true, ct);

    private async Task<UiOperationResult> UpdateSelectedCipherCollectionsAsync(
        IReadOnlyCollection<string> collectionIds,
        bool removeCollections,
        CancellationToken ct)
    {
        if (!CanManageSelectionCollections || SelectedOrganizationId is not { } organizationId)
            return RejectSelectionPlacement("所选条目不能批量更改集合。");
        if (collectionIds.Count == 0)
            return RejectSelectionPlacement("请至少选择一个可写集合。");

        if (!TryCaptureSelectedPlacementBaselines(out var baselines))
            return UiOperationResult.Failure(OperationError);
        var result = await RunBatchAsync(
            (_, token) => _service.BulkUpdateCipherCollectionsAsync(
                baselines,
                organizationId,
                collectionIds,
                removeCollections,
                token),
            ct);
        if (result.IsSuccess)
            IsSelectionMode = false;
        return result;
    }

    private bool TryCaptureSelectedPlacementBaselines(
        out CipherPlacementBaseline[] baselines)
    {
        try
        {
            baselines = _selectedIds
                .Select(_service.GetCipherPlacementBaseline)
                .ToArray();
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            baselines = [];
            OperationError = "一个或多个所选条目已发生变化，请刷新后重试。";
            return false;
        }
    }

    private UiOperationResult RejectSelectionPlacement(string fallback)
    {
        OperationError = string.IsNullOrEmpty(SelectionPlacementError)
            ? fallback
            : SelectionPlacementError;
        return UiOperationResult.Failure(OperationError);
    }

    private async Task<UiOperationResult> RunPlacementOperationAsync(
        Func<CancellationToken, Task> operation,
        string cipherId,
        CancellationToken ct)
    {
        var result = await RunWriteAndReloadAsync(
            VaultOperationKind.SaveCipher,
            operation,
            cipherId,
            ct);
        if (!result.IsSuccess)
        {
            ReloadSnapshotPreservingSelection(cipherId);
            DataStale = DataStale || _service.DataStale;
        }
        return result;
    }

    // 批量操作共用 helper:从 _selectedIds 取快照;空选择直接失败,不占用门禁、不触发服务调用。
    private async Task<UiOperationResult> RunBatchAsync(
        Func<IReadOnlyCollection<string>, CancellationToken, Task> operation,
        CancellationToken ct = default)
    {
        var ids = _selectedIds.ToArray();
        if (ids.Length == 0)
            return UiOperationResult.Failure("没有选择任何条目。");

        var selectedId = SelectedItem?.Id;
        var result = await RunWriteAndReloadAsync(
            VaultOperationKind.Batch,
            token => operation(ids, token),
            selectId: null,
            ct);
        if (!result.IsSuccess)
        {
            ReloadSnapshotPreservingSelection(selectedId);
            DataStale = DataStale || _service.DataStale;
        }
        return result;
    }

    private void ReloadItems()
    {
        Capabilities = _service.Capabilities;
        Items.Clear();
        foreach (var item in _service.GetItems())
        {
            item.LastUsedAt = _recentOrderSnapshot.TryGetValue(item.Id, out var usedAt)
                ? usedAt : _recentUseStore?.GetLastUsed(item.Id);
            Items.Add(item);
        }
        _selectedIds.IntersectWith(Items.Select(item => item.Id));
        NotifySelectionStateChanged();
    }

    private void ReloadFilters()
    {
        var tag = SelectedFilterTag;
        Filters.Clear();
        foreach (var filter in _service.GetFilters())
            Filters.Add(filter);
        AddSupplementaryFilters();
        OnPropertyChanged(nameof(FolderFilters));
        SelectFilterByTag(tag); // 复原选中(凭 tag),触发 ApplyFilter
    }

    private void AddSupplementaryFilters()
    {
        if (Filters.All(filter => filter.Kind != FilterKind.Totp))
        {
            Filters.Add(new FilterNode
            {
                Label = "验证码",
                Glyph = "\uE916",
                Kind = FilterKind.Totp,
                Count = Items.Count(item => item.HasTotp && !item.IsDeleted && !item.IsArchived),
            });
        }
    }

    private void ReloadSnapshotPreservingSelection(string? selectedId)
    {
        var wasRebuildingFilteredItems = _isRebuildingFilteredItems;
        _isRebuildingFilteredItems = true;
        try
        {
            ReloadItems();
            ReloadFilters();
        }
        finally
        {
            _isRebuildingFilteredItems = wasRebuildingFilteredItems;
        }

        if (!wasRebuildingFilteredItems)
        {
            RestoreVisibleSelection(selectedId);
            if (IsSelectionMode)
                SelectionRebindRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SelectById(string? id)
    {
        ApplyFilter();
        if (string.IsNullOrEmpty(id))
        {
            SelectedItem = null;
            return;
        }

        var item = FilteredItems.FirstOrDefault(i => i.Id == id);
        if (item is null)
        {
            SearchText = string.Empty;
            SelectedFilter = Filters.FirstOrDefault(f => f.Kind == FilterKind.AllItems) ?? Filters.FirstOrDefault();
            item = FilteredItems.FirstOrDefault(i => i.Id == id);
        }
        SelectedItem = item;
    }

    private void SelectCreatedCipher(string id)
    {
        var created = Items.FirstOrDefault(item => item.Id == id);
        if (created is null)
        {
            SelectedItem = null;
            return;
        }

        var keepScope = SelectedFilter?.Kind == FilterKind.AllItems
            || SelectedFilter is { Kind: FilterKind.Folder } folder
                && string.Equals(folder.FolderId, created.FolderId, StringComparison.Ordinal);
        if (!keepScope)
            RememberCurrentScope();

        var wasSuppressed = _suppressApply;
        _suppressApply = true;
        try
        {
            if (!keepScope)
                SelectFilterByTag("vault:allitems");
            SearchText = string.Empty;
            SelectedTypeIndex = 0;
            // Retain compatible refinements while ensuring the saved item remains discoverable.
            if (!created.HasTotp) FacetTotp = false;
            if (!created.HasAttachment) FacetAttachment = false;
            if (!created.HasUri) FacetUri = false;
            if (!created.Favorite) FacetFavoriteOnly = false;
        }
        finally { _suppressApply = wasSuppressed; }
        ApplyFilter();
        SelectedItem = FilteredItems.FirstOrDefault(item => item.Id == id);
    }

    public async Task<UiOperationResult> SyncAsync(CancellationToken ct = default)
    {
        LoadPendingCipherCreateFromLedger();
        var selectedId = SelectedItem?.Id;
        var pendingCreate = _pendingCipherCreate;
        var lifecycleEpoch = _lifecycleEpoch;
        var result = await RunOperationAsync(
            VaultOperationKind.Sync,
            async token =>
            {
                Capabilities = VaultCapabilities.None;
                CipherCreateReconciliationResult? reconciliation = null;
                if (pendingCreate is null)
                    await _service.SyncAsync(token);
                else
                    reconciliation = await _service.SyncAndReconcileCipherCreateAsync(pendingCreate, token);
                if (lifecycleEpoch != _lifecycleEpoch)
                    throw new OperationCanceledException(token);
                var reconciledId = reconciliation is
                    { Status: CipherCreateReconciliationStatus.Confirmed, ConfirmedId: { Length: > 0 } id }
                    ? id
                    : selectedId;
                ReloadSnapshotPreservingSelection(reconciledId);
                SkippedCipherCount = _service.SkippedCipherCount;
                UnsupportedOrganizationCipherCount = _service.UnsupportedOrganizationCipherCount;
                DataStale = _service.DataStale;
                if (pendingCreate is not null && reconciliation is not null)
                    ApplyCipherCreateReconciliation(pendingCreate, reconciliation);
                else if (!DataStale)
                    OperationWarning = string.Empty;
                return true;
            },
            ct);
        if (!result.IsSuccess && lifecycleEpoch == _lifecycleEpoch)
        {
            ReloadSnapshotPreservingSelection(selectedId);
            SkippedCipherCount = _service.SkippedCipherCount;
            UnsupportedOrganizationCipherCount = _service.UnsupportedOrganizationCipherCount;
            DataStale = _service.DataStale || _pendingCipherCreate is not null;
        }
        return new UiOperationResult(result.Status, result.Error);
    }

    private void ApplyCipherCreateReconciliation(
        CipherCreatePending pending,
        CipherCreateReconciliationResult reconciliation)
    {
        if (reconciliation is
            { Status: CipherCreateReconciliationStatus.Confirmed, ConfirmedId: { Length: > 0 } confirmedId })
        {
            var ownsDraft = OwnsPendingCipherCreate(pending);
            ClearPendingCipherCreate();
            if (ownsDraft)
                CloseEditor();
            var confirmedCipherIsVisible = Items.Any(item =>
                string.Equals(item.Id, confirmedId, StringComparison.Ordinal));
            if (!IsEditing)
                SelectCreatedCipher(confirmedId);
            OperationError = string.Empty;
            if (!confirmedCipherIsVisible)
            {
                OperationWarning = "新条目已确认保存，但当前无法解密或不可见。请勿重新创建。";
            }
            else if (!DataStale)
            {
                OperationWarning = string.Empty;
            }
            return;
        }

        DataStale = true;
        OperationWarning = reconciliation.Status switch
        {
            CipherCreateReconciliationStatus.NotFound =>
                "同步尚未观察到待确认条目，但先前请求可能仍在处理。请勿重复提交。",
            CipherCreateReconciliationStatus.Ambiguous =>
                "同步后找到多个可能的新条目，无法安全判断保存结果。请勿重复提交。",
            CipherCreateReconciliationStatus.SnapshotMismatch =>
                "同步快照与保存前状态不一致，仍无法安全判断新条目的保存结果。",
            CipherCreateReconciliationStatus.SessionMismatch =>
                "当前账户或服务器已变化，无法在此会话中核对新条目的保存结果。",
            _ => "仍无法安全判断新条目的保存结果。请勿重复提交。",
        };
        OperationError = HasOwnedPendingCipherCreate
            ? "新条目的保存结果仍未确认，创建该请求的草稿继续保持锁定。"
            : "新条目的保存结果仍未确认。现有草稿仍可编辑或取消，但新的创建会继续被阻止。";
    }

    [RelayCommand]
    private Task Sync() => SyncAsync();

    /// <summary>
    /// 从服务快照刷新列表（不发起网络请求），供 WebSocket 推送后 UI 增量刷新使用。
    /// </summary>
    public void RefreshFromSnapshot()
    {
        var selectedId = SelectedItem?.Id;
        ReloadSnapshotPreservingSelection(selectedId);
        SkippedCipherCount = _service.SkippedCipherCount;
        UnsupportedOrganizationCipherCount = _service.UnsupportedOrganizationCipherCount;
        // Notification-driven snapshot refreshes do not prove that the failed post-write sync
        // has recovered. Only a successful explicit SyncAsync clears the committed-write warning.
        DataStale = _service.DataStale || HasOperationWarning || _pendingCipherCreate is not null;
    }

    [RelayCommand]
    private void Add() => BeginAdd(VaultItemKind.Login);

    private bool TryGetUniformOrganizationSelection(
        out CipherListItem[] selected,
        out string? organizationId)
    {
        selected = _selectedIds
            .Select(id => Items.FirstOrDefault(item => item.Id == id))
            .Where(item => item is not null)
            .Cast<CipherListItem>()
            .ToArray();
        organizationId = null;
        if (!HasSelection || selected.Length != _selectedIds.Count)
            return false;

        var expectedOrganizationId = string.IsNullOrWhiteSpace(selected[0].OrganizationId)
            ? null
            : selected[0].OrganizationId;
        organizationId = expectedOrganizationId;
        return selected.All(item => string.Equals(
            string.IsNullOrWhiteSpace(item.OrganizationId) ? null : item.OrganizationId,
            expectedOrganizationId,
            StringComparison.Ordinal));
    }

    private bool IsPlacementTypeSupported(VaultItemKind kind) =>
        kind is VaultItemKind.Login
            or VaultItemKind.Card
            or VaultItemKind.Identity
            or VaultItemKind.Note
            or VaultItemKind.Ssh
        || (Capabilities.CanWriteNewCipherTypes && kind is
            (VaultItemKind.BankAccount
            or VaultItemKind.DriversLicense
            or VaultItemKind.Passport));

    private void NotifySelectionStateChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanModifySelection));
        OnPropertyChanged(nameof(CanDeleteSelection));
        OnPropertyChanged(nameof(CanRestoreSelection));
        OnPropertyChanged(nameof(CanMoveSelection));
        OnPropertyChanged(nameof(CanArchiveSelection));
        OnPropertyChanged(nameof(CanUnarchiveSelection));
        OnPropertyChanged(nameof(CanTransferSelectionToOrganization));
        OnPropertyChanged(nameof(CanManageSelectionCollections));
        OnPropertyChanged(nameof(SelectedOrganizationId));
        OnPropertyChanged(nameof(SelectionPlacementError));
        OnPropertyChanged(nameof(HasSelectionPlacementError));
    }

    public void ToggleSelection(string id)
    {
        if (!_selectedIds.Remove(id))
            _selectedIds.Add(id);
        NotifySelectionStateChanged();
    }

    // 由列表的 SelectionChanged 逐项调用:把 ListView 的勾选/取消精确同步进 _selectedIds
    // (此前 UI 从不同步,逐项勾选完全不被记录,批量操作全程禁用)。
    public void SetItemSelected(string id, bool selected)
    {
        var changed = selected ? _selectedIds.Add(id) : _selectedIds.Remove(id);
        if (!changed)
            return;
        NotifySelectionStateChanged();
    }

    public bool IsSelected(string id) => _selectedIds.Contains(id);

    private void ClearSelection()
    {
        _selectedIds.Clear();
        NotifySelectionStateChanged();
    }

    // 锁屏/登出时清空内存中的明文足迹:选中项、详情、编辑草稿、搜索与全部条目/过滤集合。
    // 先取消当前 Vault 操作(尽快退出,不等待网络往返),lease 失效仍是最终提交保护。
    // 幂等:重复调用安全(集合已空、属性已置默认值)。
    public void ClearSensitive()
    {
        _lifecycleEpoch++;
        _workspaceContexts.Clear();
        _workspaceVerticalOffset = 0;
        _workspaceFocusedItemId = null;
        _workspaceSearchHadFocus = false;
        _acceptCipherCreateGateNotifications = false;
        CancelCurrentOperation();
        SelectedItem = null;
        Detail = null;
        ClearPendingCipherCreate();
        _ownedPendingCipherCreateFingerprint = null;
        _explicitlyAbandonedPendingCipherCreate = null;
        SetPendingCreateAbandonedWarning(false);
        EditorDraft = null;
        _emptyEditorDisplayDraft = CipherEditorDraft.CreateDefault(VaultItemKind.Login);
        OnPropertyChanged(nameof(EditorDisplayDraft));
        _editingId = null;
        Capabilities = VaultCapabilities.None;
        OnPropertyChanged(nameof(CanChangeEditorType));
        IsEditing = false;
        SearchText = string.Empty;
        SelectedTypeIndex = 0;
        FacetTotp = false;
        FacetAttachment = false;
        FacetUri = false;
        FacetFavoriteOnly = false;
        SelectedSort = VaultSortKey.RecentUse;
        OperationError = string.Empty;
        OperationWarning = string.Empty;
        EditorError = string.Empty;
        ClearSelection();
        Items.Clear();
        _recentUses.Clear();
        _recentOrderSnapshot.Clear();
        FilteredItems.Clear();
        GroupedItems.Clear();
        Filters.Clear();
        SelectedFilter = null;
        IsSelectionMode = false;
        OnPropertyChanged(nameof(WorkspaceContext));
    }

    partial void OnIsSelectionModeChanged(bool value)
    {
        if (!value)
            ClearSelection(); // ClearSelection 已通知 HasSelection
        else
        {
            NotifySelectionStateChanged();
        }
        RebuildGroups();
    }

    [RelayCommand]
    private void ToggleSelectionMode() => IsSelectionMode = !IsSelectionMode;

    [RelayCommand]
    private void SelectAll()
    {
        if (!IsSelectionMode)
            return;
        foreach (var item in FilteredItems)
            _selectedIds.Add(item.Id);
        NotifySelectionStateChanged();
    }

    [RelayCommand]
    private async Task MoveSelectedToFolder(string? folderId)
    {
        var result = await RunBatchAsync((ids, ct) => _service.MoveCiphersAsync(ids, folderId, ct));
        if (result.IsSuccess)
            IsSelectionMode = false; // OnIsSelectionModeChanged clears selection
    }

    public Task MoveSelectedToFolderAsync(string? folderId) => MoveSelectedToFolder(folderId);

    [RelayCommand]
    private async Task SoftDeleteSelected()
    {
        var result = await RunBatchAsync((ids, ct) => _service.DeleteCiphersAsync(ids, permanent: false, ct));
        if (result.IsSuccess)
            IsSelectionMode = false;
    }

    [RelayCommand]
    private async Task RestoreSelected()
    {
        var result = await RunBatchAsync((ids, ct) => _service.RestoreCiphersAsync(ids, ct));
        if (result.IsSuccess)
            IsSelectionMode = false;
    }

    [RelayCommand]
    private async Task ArchiveSelected()
    {
        if (!CanArchiveSelection)
            return;
        var result = await RunBatchAsync((ids, ct) => _service.ArchiveCiphersAsync(ids, ct));
        if (result.IsSuccess)
            IsSelectionMode = false;
    }

    [RelayCommand]
    private async Task UnarchiveSelected()
    {
        if (!CanUnarchiveSelection)
            return;
        var result = await RunBatchAsync((ids, ct) => _service.UnarchiveCiphersAsync(ids, ct));
        if (result.IsSuccess)
            IsSelectionMode = false;
    }

    // 注：破坏性确认在 XAML code-behind 先弹 ContentDialog，确认后才执行此命令。
    [RelayCommand]
    private async Task PermanentDeleteSelected()
    {
        var result = await RunBatchAsync((ids, ct) => _service.DeleteCiphersAsync(ids, permanent: true, ct));
        if (result.IsSuccess)
            IsSelectionMode = false;
    }

    // 注：清空确认在 XAML code-behind 先弹 ContentDialog，确认后才执行此命令。清空回收站不依赖
    // 选择集(操作全部已删除条目),故不经 RunBatchAsync,直接复用 RunWriteAndReloadAsync。
    [RelayCommand]
    private Task<UiOperationResult> EmptyRecycleBinAsync()
    {
        var ids = Items.Where(i => i.IsDeleted).Select(i => i.Id).ToArray();
        if (ids.Length == 0)
            return Task.FromResult(UiOperationResult.Failure("回收站为空。"));
        return RunWriteAndReloadAsync(
            VaultOperationKind.Batch,
            token => _service.DeleteCiphersAsync(ids, permanent: true, token),
            selectId: null,
            CancellationToken.None);
    }

    [RelayCommand]
    private void AddCustomField()
    {
        var draft = EditorDraft;
        if (draft is null)
            return;

        draft.CustomFields.Add(new CustomFieldEditorDraft
        {
            Name = $"字段 {draft.CustomFields.Count + 1}",
        });
    }

    [RelayCommand]
    private void RemoveCustomField(CustomFieldEditorDraft? field)
    {
        if (EditorDraft is null || field is null)
            return;

        EditorDraft.CustomFields.Remove(field);
    }

    [RelayCommand]
    private void Copy(string? value)
    {
        // IClipboardService applies the protected vault policy to non-secret fields as well.
        if (!string.IsNullOrEmpty(value))
        {
            var result = WriteClipboard(value, isSecret: false);
            if (result.IsSuccess && SelectedItem is { } item)
                RecordRecentUse(item.Id);
        }
    }

    private UiOperationResult WriteClipboard(string value, bool isSecret)
    {
        if (_clipboard is null)
        {
            OperationError = "剪贴板暂不可用，请重试。";
            return UiOperationResult.Failure(OperationError);
        }

        try
        {
            if (isSecret)
                _clipboard.SetSecretText(value);
            else
                _clipboard.SetText(value);
            OperationError = string.Empty;
            return UiOperationResult.Success();
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException
            or InvalidOperationException)
        {
            // Clipboard contention is recoverable; provider exception messages may contain input.
            OperationError = "复制失败，剪贴板暂不可用。请重试。";
            return UiOperationResult.Failure(OperationError);
        }
    }

    // 复制主值必须先过条目级门禁:未授权/被拒时绝不读取敏感详情、绝不写剪贴板。
    public async Task<UiOperationResult> CopyPrimaryAsync(
        string? id,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default)
    {
        var lifecycleEpoch = _lifecycleEpoch;
        if (string.IsNullOrEmpty(id))
            return UiOperationResult.Failure("无效条目");
        if (Items.FirstOrDefault(item => item.Id == id) is not { CanViewPassword: true })
            return UiOperationResult.Failure("该组织条目已隐藏密码，不能复制敏感值。");

        var authorization = await EnsureAuthorizedAsync(
            id, SensitiveItemAction.Copy, prompt, ct);
        if (!authorization.IsSuccess)
            return new UiOperationResult(authorization.Status, authorization.Error);
        if (ct.IsCancellationRequested || lifecycleEpoch != _lifecycleEpoch)
            return UiOperationResult.Cancelled();
        if (Items.FirstOrDefault(item => item.Id == id) is not { CanViewPassword: true })
            return UiOperationResult.Failure("该条目已不可用，请刷新后重试。");

        var value = PrimaryValue(_service.GetDetail(id, includeSensitive: true));
        if (!string.IsNullOrEmpty(value))
            return WriteItemClipboard(id, value, isSecret: true);
        return UiOperationResult.Failure("没有可复制的内容。");
    }

    public async Task<UiOperationResult> CopyPasswordAsync(
        string id,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default)
    {
        var lifecycleEpoch = _lifecycleEpoch;
        if (Items.FirstOrDefault(item => item.Id == id) is not { Kind: VaultItemKind.Login, CanViewPassword: true })
            return UiOperationResult.Failure("此条目不允许复制密码。");
        var authorization = await EnsureAuthorizedAsync(id, SensitiveItemAction.Copy, prompt, ct);
        if (!authorization.IsSuccess)
            return new UiOperationResult(authorization.Status, authorization.Error);
        if (ct.IsCancellationRequested || lifecycleEpoch != _lifecycleEpoch)
            return UiOperationResult.Cancelled();
        if (Items.FirstOrDefault(item => item.Id == id) is not { CanViewPassword: true })
            return UiOperationResult.Failure("该条目已不可用，请刷新后重试。");

        if (_service.GetDetail(id, includeSensitive: true) is not LoginDetail { Password: { Length: > 0 } password })
            return UiOperationResult.Failure("未设置密码。");
        return WriteItemClipboard(id, password, isSecret: true);
    }

    private UiOperationResult WriteItemClipboard(string id, string value, bool isSecret)
    {
        var result = WriteClipboard(value, isSecret);
        if (result.IsSuccess)
            RecordRecentUse(id);
        return result;
    }

    public async Task<UiOperationResult> CopyFieldAsync(
        string id,
        string key,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default)
    {
        var lifecycleEpoch = _lifecycleEpoch;
        var isSecret = key is "password" or "totp" or "number" or "cvv" or "privateKey";
        var item = Items.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null || (isSecret && !item.CanViewPassword))
            return UiOperationResult.Failure("此条目不允许复制该字段。");
        var authorization = await EnsureAuthorizedAsync(id, SensitiveItemAction.Copy, prompt, ct);
        if (!authorization.IsSuccess)
            return new UiOperationResult(authorization.Status, authorization.Error);
        if (ct.IsCancellationRequested || lifecycleEpoch != _lifecycleEpoch)
            return UiOperationResult.Cancelled();
        item = Items.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null || (isSecret && !item.CanViewPassword))
            return UiOperationResult.Failure("该条目的访问权限已发生变化。");

        CipherDetail detail;
        try { detail = _service.GetDetail(id, includeSensitive: true); }
        catch (InvalidOperationException)
        {
            return UiOperationResult.Failure("该条目已不可用，请刷新后重试。");
        }

        var value = (detail, key) switch
        {
            (LoginDetail login, "username") => login.Username,
            (LoginDetail login, "password") => login.Password,
            (LoginDetail login, "totp") => Crypto.TotpGenerator.TryGenerate(
                login.TotpSecret, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out var code) ? code : null,
            (CardDetail card, "number") => card.Number,
            (CardDetail card, "expiry") => card.Expiry,
            (CardDetail card, "cvv") => card.Cvv,
            (IdentityDetail identity, "fullName") => identity.FullName,
            (IdentityDetail identity, "phone") => identity.Phone,
            (IdentityDetail identity, "email") => identity.Email,
            (IdentityDetail identity, "username") => identity.Username,
            (SshDetail ssh, "publicKey") => ssh.PublicKey,
            (SshDetail ssh, "fingerprint") => ssh.Fingerprint,
            (SshDetail ssh, "privateKey") => ssh.PrivateKey,
            _ => null,
        };
        return string.IsNullOrEmpty(value)
            ? UiOperationResult.Failure(key == "totp" ? "验证码不可用。" : "此字段没有可复制的内容。")
            : WriteItemClipboard(id, value, isSecret);
    }

    public CipherDetail? ReadAuthorizedDetail(string id)
    {
        if (SelectedItem?.Id != id || Items.FirstOrDefault(candidate => candidate.Id == id) is not { } item)
            return null;
        try
        {
            var policy = _service.GetAccessPolicy(id);
            if (policy.RequiresReprompt && _reprompt?.IsAuthorized(policy) != true)
                return null;
            // The service also applies the current organization policy when projecting secrets.
            return _service.GetDetail(id, includeSensitive: item.CanViewPassword);
        }
        catch (InvalidOperationException) { return null; }
    }

    public async Task<bool> AuthorizeFieldCopyAsync(
        string id,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default)
    {
        var lifecycleEpoch = _lifecycleEpoch;
        if (SelectedItem?.Id != id || Items.All(item => item.Id != id) || ct.IsCancellationRequested)
            return false;
        try
        {
            var result = await EnsureAuthorizedAsync(id, SensitiveItemAction.Copy, prompt, ct);
            if (!result.IsSuccess)
            {
                if (result.Status == UiOperationStatus.Failed)
                    OperationError = result.Error ?? "无法完成条目验证，请重试。";
                return false;
            }
            return !ct.IsCancellationRequested
                && lifecycleEpoch == _lifecycleEpoch && SelectedItem?.Id == id
                && Items.Any(item => item.Id == id);
        }
        catch (OperationCanceledException exception) when (UiExceptionPolicy.IsGenuineCancellation(exception, ct))
        {
            return false;
        }
        catch (Exception exception) when (Api.RequestTimeoutClassifier.IsTimeout(exception))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return false;
        }
        catch (InvalidOperationException) { return false; }
    }

    // 各类型主值:登录→密码(空则用户名),卡→卡号,笔记→内容,SSH→公钥,身份等→名称。
    private static string? PrimaryValue(CipherDetail detail) => detail switch
    {
        LoginDetail l => string.IsNullOrEmpty(l.Password) ? l.Username : l.Password,
        CardDetail c => c.Number,
        NoteDetail n => n.Content,
        SshDetail s => s.PublicKey,
        _ => detail.Name,
    };

    // ── 条目级主密码重提示门禁 ───────────────────────────────────────────────────
    // 解锁当前详情的敏感字段:先过条目级门禁(动作 Totp——授权前 TotpSecret 恒为 null),
    // 成功后才用 includeSensitive 重投影 Detail。被拒/取消时 Detail 维持脱敏投影不变。
    public async Task<UiOperationResult<bool>> UnlockDetailAsync(
        string id,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default)
    {
        var lifecycleEpoch = _lifecycleEpoch;
        if (Items.FirstOrDefault(item => item.Id == id) is not { CanViewPassword: true })
            return UiOperationResult<bool>.Failure("该组织条目已隐藏密码，不能显示敏感字段。");

        var authorization = await EnsureAuthorizedAsync(
            id, SensitiveItemAction.Totp, prompt, ct);
        if (!authorization.IsSuccess)
            return authorization;
        if (ct.IsCancellationRequested || lifecycleEpoch != _lifecycleEpoch)
            return UiOperationResult<bool>.Cancelled();
        if (Items.FirstOrDefault(item => item.Id == id) is not { CanViewPassword: true })
            return UiOperationResult<bool>.Failure("该条目的访问权限已发生变化。");

        if (SelectedItem?.Id == id || Detail?.Id == id)
        {
            Detail = _service.GetDetail(id, includeSensitive: true);
            OnPropertyChanged(nameof(DetailIconDomain));
        }
        return UiOperationResult<bool>.Success(true);
    }

    public Task<UiOperationResult> ToggleFavoriteAsync(string id, CancellationToken ct = default) =>
        RunWriteAndReloadAsync(
            VaultOperationKind.SaveCipher,
            token => _service.SetFavoriteAsync(
                id,
                !(Items.FirstOrDefault(item => item.Id == id)?.Favorite
                    ?? throw new InvalidOperationException("Cipher is not present in the current vault view.")),
                token),
            selectId: id,
            ct);

    public async Task<UiOperationResult> AddAttachmentAsync(
        string cipherId,
        Core.Models.ReopenableFileSource file,
        CancellationToken ct = default)
    {
        if (_attachments is null)
            return UiOperationResult.Failure("附件服务不可用");

        var result = await RunOperationAsync(
            VaultOperationKind.AttachmentUpload,
            async token =>
            {
                var attachments = await _attachments.AddAttachmentAsync(cipherId, file, token);
                RefreshDetailAttachments(cipherId, attachments);
                return true;
            },
            ct,
            committedRefreshFailure: () => true);
        return new UiOperationResult(result.Status, result.Error);
    }

    // 下载附件前必须先过条目级门禁:被拒/取消时绝不调用附件服务、绝不占用操作门禁。
    // 门禁原子去重交由 RunOperationAsync 的 TryEnter 完成,无需额外 IsBusy 预检查。
    public async Task<UiOperationResult<long>> DownloadAttachmentAsync(
        string cipherId,
        string attachmentId,
        Func<CancellationToken, Task<string?>> prompt,
        Stream plaintextDestination,
        CancellationToken ct = default)
    {
        var authorization = await EnsureAuthorizedAsync(
            cipherId, SensitiveItemAction.DownloadAttachment, prompt, ct);
        if (!authorization.IsSuccess)
            return new UiOperationResult<long>(authorization.Status, default, authorization.Error);
        if (_attachments is null)
            return UiOperationResult<long>.Failure("附件服务不可用");

        return await RunOperationAsync(
            VaultOperationKind.AttachmentDownload,
            token => _attachments.DownloadAttachmentAsync(
                cipherId,
                attachmentId,
                plaintextDestination,
                token),
            ct);
    }

    public async Task<UiOperationResult> DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default)
    {
        if (_attachments is null)
            return UiOperationResult.Failure("附件服务不可用");

        var result = await RunOperationAsync(
            VaultOperationKind.AttachmentDelete,
            async token =>
            {
                var attachments = await _attachments.DeleteAttachmentAsync(cipherId, attachmentId, token);
                RefreshDetailAttachments(cipherId, attachments);
                return true;
            },
            ct,
            committedRefreshFailure: () => true);
        return new UiOperationResult(result.Status, result.Error);
    }

    // 当前选中详情就是被改动的条目时,用最新附件列表重投影 Detail。
    // CipherDetail.Attachments 是 init-only,故用 _service.GetDetail 重建详情(re-sync 后快照已含新附件),
    // 再以服务刚返回的 attachments 覆盖,避免快照/服务返回不一致时显示陈旧数据。
    private void RefreshDetailAttachments(string cipherId, IReadOnlyList<AttachmentItem> attachments)
    {
        if (Detail is null || Detail.Id != cipherId)
            return;

        // 保留当前解锁状态重投影:锁定详情仍脱敏,已解锁详情仍显示敏感字段。
        var includeSensitive = Detail.SensitiveUnlocked;
        Detail = _service.GetDetail(cipherId, includeSensitive) is { } refreshed && refreshed.Id == cipherId
            ? CloneDetailWithAttachments(refreshed, attachments)
            : Detail;
        OnPropertyChanged(nameof(DetailIconDomain));
    }

    private static CipherDetail CloneDetailWithAttachments(CipherDetail detail, IReadOnlyList<AttachmentItem> attachments) => detail switch
    {
        LoginDetail l => new LoginDetail
        {
            Id = l.Id, Name = l.Name, FolderName = l.FolderName, Notes = l.Notes, CustomFields = l.CustomFields,
            Created = l.Created, Edited = l.Edited, IsDeleted = l.IsDeleted, IsArchived = l.IsArchived,
            CanEdit = l.CanEdit, CanDelete = l.CanDelete, CanRestore = l.CanRestore,
            Favorite = l.Favorite, Reprompt = l.Reprompt,
            SensitiveUnlocked = l.SensitiveUnlocked,
            Username = l.Username, Password = l.Password, TotpSecret = l.TotpSecret, Uri = l.Uri, Uris = l.Uris, Passkeys = l.Passkeys,
            PasswordHistory = l.PasswordHistory,
            PasswordRevisionDate = l.PasswordRevisionDate,
            Attachments = attachments,
        },
        CardDetail c => new CardDetail
        {
            Id = c.Id, Name = c.Name, FolderName = c.FolderName, Notes = c.Notes, CustomFields = c.CustomFields,
            Created = c.Created, Edited = c.Edited, IsDeleted = c.IsDeleted, IsArchived = c.IsArchived,
            CanEdit = c.CanEdit, CanDelete = c.CanDelete, CanRestore = c.CanRestore,
            Favorite = c.Favorite, Reprompt = c.Reprompt,
            SensitiveUnlocked = c.SensitiveUnlocked,
            Cardholder = c.Cardholder, Number = c.Number, Expiry = c.Expiry, Brand = c.Brand, Cvv = c.Cvv,
            Attachments = attachments,
        },
        IdentityDetail i => new IdentityDetail
        {
            Id = i.Id, Name = i.Name, FolderName = i.FolderName, Notes = i.Notes, CustomFields = i.CustomFields,
            Created = i.Created, Edited = i.Edited, IsDeleted = i.IsDeleted, IsArchived = i.IsArchived,
            CanEdit = i.CanEdit, CanDelete = i.CanDelete, CanRestore = i.CanRestore,
            Favorite = i.Favorite, Reprompt = i.Reprompt,
            SensitiveUnlocked = i.SensitiveUnlocked,
            FullName = i.FullName, Username = i.Username, Company = i.Company, Email = i.Email, Phone = i.Phone,
            IdNumber = i.IdNumber, PassportNumber = i.PassportNumber, LicenseNumber = i.LicenseNumber, Address = i.Address,
            Attachments = attachments,
        },
        NoteDetail n => new NoteDetail
        {
            Id = n.Id, Name = n.Name, FolderName = n.FolderName, Notes = n.Notes, CustomFields = n.CustomFields,
            Created = n.Created, Edited = n.Edited, IsDeleted = n.IsDeleted, IsArchived = n.IsArchived,
            CanEdit = n.CanEdit, CanDelete = n.CanDelete, CanRestore = n.CanRestore,
            Favorite = n.Favorite, Reprompt = n.Reprompt,
            SensitiveUnlocked = n.SensitiveUnlocked,
            Content = n.Content,
            Attachments = attachments,
        },
        SshDetail s => new SshDetail
        {
            Id = s.Id, Name = s.Name, FolderName = s.FolderName, Notes = s.Notes, CustomFields = s.CustomFields,
            Created = s.Created, Edited = s.Edited, IsDeleted = s.IsDeleted, IsArchived = s.IsArchived,
            CanEdit = s.CanEdit, CanDelete = s.CanDelete, CanRestore = s.CanRestore,
            Favorite = s.Favorite, Reprompt = s.Reprompt,
            SensitiveUnlocked = s.SensitiveUnlocked,
            PublicKey = s.PublicKey, PrivateKey = s.PrivateKey, Fingerprint = s.Fingerprint,
            Attachments = attachments,
        },
        BankAccountDetail bank => new BankAccountDetail
        {
            Id = bank.Id, Name = bank.Name, FolderName = bank.FolderName, Notes = bank.Notes,
            CustomFields = bank.CustomFields, Created = bank.Created, Edited = bank.Edited,
            IsDeleted = bank.IsDeleted, IsArchived = bank.IsArchived, CanEdit = bank.CanEdit,
            CanDelete = bank.CanDelete, CanRestore = bank.CanRestore, Favorite = bank.Favorite,
            Reprompt = bank.Reprompt, SensitiveUnlocked = bank.SensitiveUnlocked,
            BankName = bank.BankName, NameOnAccount = bank.NameOnAccount, AccountType = bank.AccountType,
            AccountNumber = bank.AccountNumber, RoutingNumber = bank.RoutingNumber,
            BranchNumber = bank.BranchNumber, Pin = bank.Pin, SwiftCode = bank.SwiftCode,
            Iban = bank.Iban, BankContactPhone = bank.BankContactPhone, Attachments = attachments,
        },
        DriversLicenseDetail license => new DriversLicenseDetail
        {
            Id = license.Id, Name = license.Name, FolderName = license.FolderName, Notes = license.Notes,
            CustomFields = license.CustomFields, Created = license.Created, Edited = license.Edited,
            IsDeleted = license.IsDeleted, IsArchived = license.IsArchived, CanEdit = license.CanEdit,
            CanDelete = license.CanDelete, CanRestore = license.CanRestore, Favorite = license.Favorite,
            Reprompt = license.Reprompt, SensitiveUnlocked = license.SensitiveUnlocked,
            FullName = license.FullName, DateOfBirth = license.DateOfBirth,
            LicenseNumber = license.LicenseNumber, IssuingCountry = license.IssuingCountry,
            IssuingState = license.IssuingState, IssueDate = license.IssueDate,
            ExpirationDate = license.ExpirationDate, IssuingAuthority = license.IssuingAuthority,
            LicenseClass = license.LicenseClass, Attachments = attachments,
        },
        PassportDetail passport => new PassportDetail
        {
            Id = passport.Id, Name = passport.Name, FolderName = passport.FolderName, Notes = passport.Notes,
            CustomFields = passport.CustomFields, Created = passport.Created, Edited = passport.Edited,
            IsDeleted = passport.IsDeleted, IsArchived = passport.IsArchived, CanEdit = passport.CanEdit,
            CanDelete = passport.CanDelete, CanRestore = passport.CanRestore, Favorite = passport.Favorite,
            Reprompt = passport.Reprompt, SensitiveUnlocked = passport.SensitiveUnlocked,
            FullName = passport.FullName, DateOfBirth = passport.DateOfBirth, Sex = passport.Sex,
            BirthPlace = passport.BirthPlace, Nationality = passport.Nationality,
            IssuingCountry = passport.IssuingCountry, PassportNumber = passport.PassportNumber,
            PassportType = passport.PassportType,
            NationalIdentificationNumber = passport.NationalIdentificationNumber,
            IssuingAuthority = passport.IssuingAuthority, IssueDate = passport.IssueDate,
            ExpirationDate = passport.ExpirationDate, Attachments = attachments,
        },
        _ => detail,
    };
}
