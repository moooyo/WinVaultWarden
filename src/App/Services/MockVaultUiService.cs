using App.ViewModels.Models;
using Core.Models;

namespace App.Services;

// App 层 UI 数据服务(mock)。与 Core.IVaultService 分开:这是给界面用的展示数据。
// 纯 C#(只依赖 App 视图模型,不依赖 WinUI),便于被 App.Tests 链接测试。
public interface IVaultUiService
{
    VaultCapabilities Capabilities => VaultCapabilities.None;

    IReadOnlyList<CipherListItem> GetItems();

    // 条目级访问策略:该条目是否要求主密码重提示。授权判定的唯一输入,绝不携带任何秘密。
    CipherAccessPolicy GetAccessPolicy(string id);

    // 详情投影。includeSensitive==false(或未授权)时返回脱敏投影:密码/TOTP/隐藏字段值/密码历史等一律抹除。
    // Reprompt==false 的条目无论传入何值都返回完整投影(不受门禁约束)。
    CipherDetail GetDetail(string id, bool includeSensitive);

    IReadOnlyList<FilterNode> GetFilters();
    CipherEditorDraft GetDraft(string id);
    IReadOnlyList<CipherDestinationOption> GetCipherDestinations() =>
        (CipherDestinationOption[])[CipherDestinationOption.Personal];
    IReadOnlyList<CipherCollectionOption> GetCipherCollectionOptions(
        string organizationId,
        IReadOnlyCollection<string>? selectedCollectionIds = null,
        bool lockReadOnlyAssignments = false) => [];
    CipherPlacementBaseline GetCipherPlacementBaseline(string id) =>
        throw new NotSupportedException("Cipher placement is not supported by this UI service.");

    // 解密同步时被跳过(无法解密)的条目数,供 UI 提示用户有条目被隐藏。
    int SkippedCipherCount { get; }

    int UnsupportedOrganizationCipherCount => 0;

    // 当前快照是否来自离线缓存(而非刚完成的联网同步),供 UI 提示"数据可能过期"。
    bool DataStale { get; }

    // 写入侧:每个方法 = 加密 → 写 API → 整库 re-sync(真实实现委托 IVaultWriteService)。
    Task<string> SaveCipherAsync(CipherEditorDraft draft, string? editingId, CancellationToken ct = default);
    CipherCreatePending? GetPendingCipherCreate() => null;
    CipherCreatePending RememberPendingCipherCreate(CipherCreatePending pending) => pending;
    bool HasPendingOrInFlightCipherCreate() => GetPendingCipherCreate() is not null;
    bool AbandonPendingCipherCreate(CipherCreatePending expected) => false;
    event EventHandler? CipherCreateGateChanged
    {
        add { }
        remove { }
    }
    Task<CipherCreateReconciliationResult> SyncAndReconcileCipherCreateAsync(
        CipherCreatePending pending,
        CancellationToken ct = default) =>
        Task.FromException<CipherCreateReconciliationResult>(
            new NotSupportedException("Cipher create reconciliation is not supported by this UI service."));
    Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default);
    Task RestoreCipherAsync(string id, CancellationToken ct = default);
    Task SetFavoriteAsync(string id, bool favorite, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Partial cipher updates are not supported by this UI service."));
    Task ArchiveCipherAsync(string id, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Cipher archiving is not supported by this UI service."));
    Task UnarchiveCipherAsync(string id, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Cipher unarchiving is not supported by this UI service."));
    Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default);
    Task DeleteFolderAsync(string folderId, CancellationToken ct = default);
    Task SyncAsync(CancellationToken ct = default);
    Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default);
    Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default);
    Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default);
    Task TransferCipherToOrganizationAsync(
        CipherPlacementBaseline baseline,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Cipher organization transfer is not supported by this UI service."));
    Task SetCipherCollectionsAsync(
        CipherPlacementBaseline baseline,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Cipher collection assignment is not supported by this UI service."));
    Task TransferCiphersToOrganizationAsync(
        IReadOnlyCollection<CipherPlacementBaseline> baselines,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Bulk cipher organization transfer is not supported by this UI service."));
    Task BulkUpdateCipherCollectionsAsync(
        IReadOnlyCollection<CipherPlacementBaseline> baselines,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        bool removeCollections,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Bulk cipher collection assignment is not supported by this UI service."));
    Task ArchiveCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Bulk cipher archiving is not supported by this UI service."));
    Task UnarchiveCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("Bulk cipher unarchiving is not supported by this UI service."));
}

public sealed class MockVaultUiService : IVaultUiService
{
    private const string GlyphAllItems = "\uE71D";
    private const string GlyphFavorite = "\uE734";
    private const string GlyphTrash = "\uE74D";
    private const string GlyphArchive = "\uE7B8";
    private const string GlyphLogin = "\uE8D7";
    private const string GlyphCard = "\uE8C7";
    private const string GlyphIdentity = "\uE77B";
    private const string GlyphNote = "\uE70B";
    private const string GlyphSsh = "\uE192";
    private const string GlyphFolder = "\uE8B7";
    private const string GlyphBankAccount = "\uE825";
    private const string GlyphDriversLicense = "\uE77B";
    private const string GlyphPassport = "\uE8A5";

    private readonly List<CipherDetail> _details = BuildDetails();
    private readonly Dictionary<string, string?> _folderIds = new() { ["1"] = "f1" };
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal) { "6" };
    private readonly HashSet<string> _archived = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _favoriteOverrides = new(StringComparer.Ordinal);
    private readonly List<(string Id, string Name)> _folders = new() { ("f1", "文件夹1") };
    private int _nextId = 100;

    public MockVaultUiService(VaultCapabilities? capabilities = null)
    {
        Capabilities = capabilities ?? new VaultCapabilities(
            CanWriteNewCipherTypes: false,
            SupportsArchiveProtocol: true,
            HasArchiveEntitlement: true);
    }

    public int SkippedCipherCount => 0;
    public int UnsupportedOrganizationCipherCount => 0;
    public bool DataStale => false;
    public VaultCapabilities Capabilities { get; set; }

    public IReadOnlyList<CipherDestinationOption> GetCipherDestinations() =>
        (CipherDestinationOption[])[CipherDestinationOption.Personal];

    public IReadOnlyList<CipherListItem> GetItems() =>
        _details.Select(d => new CipherListItem
        {
            Id = d.Id,
            Name = d.Name,
            Kind = d.Kind,
            Subtitle = SubtitleFor(d),
            Glyph = GlyphFor(d.Kind),
            Favorite = _favoriteOverrides.TryGetValue(d.Id, out var favorite) ? favorite : d.Favorite,
            FolderId = _folderIds.TryGetValue(d.Id, out var folderId) ? folderId : null,
            CanClone = true,
            CanArchive = Capabilities.CanArchive,
            CanUnarchive = Capabilities.CanUnarchive,
            IsDeleted = _deleted.Contains(d.Id),
            IsArchived = _archived.Contains(d.Id),
            SearchHaystack = BuildHaystack(d),
            HasTotp = d is LoginDetail { TotpSecret.Length: > 0 },
            HasAttachment = d.Attachments.Count > 0,
            HasUri = d is LoginDetail { Uri.Length: > 0 },
            RevisionDate = d.Edited,
            CreationDate = d.Created,
        }).ToList();

    // Mock 版 haystack:与 Vault.VaultSearch.BuildHaystack 同思路(名称/备注/登录用户名与 URI/自定义字段名+非隐藏值),
    // 供搜索测试(SearchText 命中 haystack)在 mock 数据上也生效。不含密码/隐藏字段值/CVV。
    private static string BuildHaystack(CipherDetail d)
    {
        var parts = new List<string?> { d.Name, d.Notes };
        if (d is LoginDetail login)
        {
            parts.Add(login.Username);
            parts.Add(login.Uri);
        }
        foreach (var f in d.CustomFields)
        {
            parts.Add(f.Label);
            if (!f.IsSecret) parts.Add(f.Value);
        }
        return string.Join('\n', parts.Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant();
    }

    public CipherAccessPolicy GetAccessPolicy(string id)
    {
        var cipher = _details.First(d => d.Id == id);
        return new CipherAccessPolicy(cipher.Id, cipher.Reprompt);
    }

    // 脱敏投影:Reprompt==true 且未授权(includeSensitive==false)时抹除全部秘密;
    // 与真实 VaultUiService 同一规则,测试环境不得绕过门禁。
    public CipherDetail GetDetail(string id, bool includeSensitive)
    {
        var cipher = _details.First(d => d.Id == id);
        return Project(cipher, includeSensitive || !cipher.Reprompt);
    }

    private static CipherDetail Project(CipherDetail d, bool unlock)
    {
        var fields = unlock ? d.CustomFields : MaskFields(d.CustomFields);
        return d switch
        {
            LoginDetail l => new LoginDetail
            {
                Id = l.Id, Name = l.Name, FolderName = l.FolderName, CustomFields = fields,
                Created = l.Created, Edited = l.Edited, IsDeleted = l.IsDeleted, Favorite = l.Favorite,
                IsArchived = l.IsArchived, CanEdit = l.CanEdit, CanDelete = l.CanDelete, CanRestore = l.CanRestore,
                Reprompt = l.Reprompt, SensitiveUnlocked = unlock, Attachments = l.Attachments,
                Username = l.Username, Uri = l.Uri, Passkeys = l.Passkeys,
                Notes = unlock ? l.Notes : null,
                Password = unlock ? l.Password : null,
                TotpSecret = unlock ? l.TotpSecret : null,
                PasswordHistory = unlock ? l.PasswordHistory : Array.Empty<PasswordHistoryItem>(),
            },
            CardDetail c => new CardDetail
            {
                Id = c.Id, Name = c.Name, FolderName = c.FolderName, CustomFields = fields,
                Created = c.Created, Edited = c.Edited, IsDeleted = c.IsDeleted, Favorite = c.Favorite,
                IsArchived = c.IsArchived, CanEdit = c.CanEdit, CanDelete = c.CanDelete, CanRestore = c.CanRestore,
                Reprompt = c.Reprompt, SensitiveUnlocked = unlock, Attachments = c.Attachments,
                Cardholder = c.Cardholder, Expiry = c.Expiry, Brand = c.Brand,
                Notes = unlock ? c.Notes : null,
                Number = unlock ? c.Number : null,
                Cvv = unlock ? c.Cvv : null,
            },
            IdentityDetail i => new IdentityDetail
            {
                Id = i.Id, Name = i.Name, FolderName = i.FolderName, CustomFields = fields,
                Created = i.Created, Edited = i.Edited, IsDeleted = i.IsDeleted, Favorite = i.Favorite,
                IsArchived = i.IsArchived, CanEdit = i.CanEdit, CanDelete = i.CanDelete, CanRestore = i.CanRestore,
                Reprompt = i.Reprompt, SensitiveUnlocked = unlock, Attachments = i.Attachments,
                FullName = i.FullName, Username = i.Username, Company = i.Company, Email = i.Email,
                Phone = i.Phone, Address = i.Address,
                Notes = unlock ? i.Notes : null,
                IdNumber = unlock ? i.IdNumber : null,
            },
            NoteDetail n => new NoteDetail
            {
                Id = n.Id, Name = n.Name, FolderName = n.FolderName, CustomFields = fields,
                Created = n.Created, Edited = n.Edited, IsDeleted = n.IsDeleted, Favorite = n.Favorite,
                IsArchived = n.IsArchived, CanEdit = n.CanEdit, CanDelete = n.CanDelete, CanRestore = n.CanRestore,
                Reprompt = n.Reprompt, SensitiveUnlocked = unlock, Attachments = n.Attachments,
                Notes = unlock ? n.Notes : null,
                Content = unlock ? n.Content : null,
            },
            SshDetail s => new SshDetail
            {
                Id = s.Id, Name = s.Name, FolderName = s.FolderName, CustomFields = fields,
                Created = s.Created, Edited = s.Edited, IsDeleted = s.IsDeleted, Favorite = s.Favorite,
                IsArchived = s.IsArchived, CanEdit = s.CanEdit, CanDelete = s.CanDelete, CanRestore = s.CanRestore,
                Reprompt = s.Reprompt, SensitiveUnlocked = unlock, Attachments = s.Attachments,
                PublicKey = s.PublicKey, Fingerprint = s.Fingerprint,
                Notes = unlock ? s.Notes : null,
                PrivateKey = unlock ? s.PrivateKey : null,
            },
            BankAccountDetail bank => new BankAccountDetail
            {
                Id = bank.Id, Name = bank.Name, FolderName = bank.FolderName, CustomFields = fields,
                Created = bank.Created, Edited = bank.Edited, IsDeleted = bank.IsDeleted,
                IsArchived = bank.IsArchived, CanEdit = bank.CanEdit, CanDelete = bank.CanDelete,
                CanRestore = bank.CanRestore, Favorite = bank.Favorite, Reprompt = bank.Reprompt,
                SensitiveUnlocked = unlock, Attachments = bank.Attachments,
                BankName = bank.BankName, NameOnAccount = bank.NameOnAccount, AccountType = bank.AccountType,
                BankContactPhone = bank.BankContactPhone, Notes = unlock ? bank.Notes : null,
                AccountNumber = unlock ? bank.AccountNumber : null,
                RoutingNumber = unlock ? bank.RoutingNumber : null,
                BranchNumber = unlock ? bank.BranchNumber : null,
                Pin = unlock ? bank.Pin : null, SwiftCode = unlock ? bank.SwiftCode : null,
                Iban = unlock ? bank.Iban : null,
            },
            DriversLicenseDetail license => new DriversLicenseDetail
            {
                Id = license.Id, Name = license.Name, FolderName = license.FolderName, CustomFields = fields,
                Created = license.Created, Edited = license.Edited, IsDeleted = license.IsDeleted,
                IsArchived = license.IsArchived, CanEdit = license.CanEdit, CanDelete = license.CanDelete,
                CanRestore = license.CanRestore, Favorite = license.Favorite, Reprompt = license.Reprompt,
                SensitiveUnlocked = unlock, Attachments = license.Attachments, FullName = license.FullName,
                IssuingCountry = license.IssuingCountry, IssuingState = license.IssuingState,
                IssuingAuthority = license.IssuingAuthority, LicenseClass = license.LicenseClass,
                Notes = unlock ? license.Notes : null, DateOfBirth = unlock ? license.DateOfBirth : null,
                LicenseNumber = unlock ? license.LicenseNumber : null,
                IssueDate = unlock ? license.IssueDate : null,
                ExpirationDate = unlock ? license.ExpirationDate : null,
            },
            PassportDetail passport => new PassportDetail
            {
                Id = passport.Id, Name = passport.Name, FolderName = passport.FolderName, CustomFields = fields,
                Created = passport.Created, Edited = passport.Edited, IsDeleted = passport.IsDeleted,
                IsArchived = passport.IsArchived, CanEdit = passport.CanEdit, CanDelete = passport.CanDelete,
                CanRestore = passport.CanRestore, Favorite = passport.Favorite, Reprompt = passport.Reprompt,
                SensitiveUnlocked = unlock, Attachments = passport.Attachments, FullName = passport.FullName,
                Nationality = passport.Nationality, IssuingCountry = passport.IssuingCountry,
                PassportType = passport.PassportType, IssuingAuthority = passport.IssuingAuthority,
                Notes = unlock ? passport.Notes : null, DateOfBirth = unlock ? passport.DateOfBirth : null,
                Sex = unlock ? passport.Sex : null, BirthPlace = unlock ? passport.BirthPlace : null,
                PassportNumber = unlock ? passport.PassportNumber : null,
                NationalIdentificationNumber = unlock ? passport.NationalIdentificationNumber : null,
                IssueDate = unlock ? passport.IssueDate : null,
                ExpirationDate = unlock ? passport.ExpirationDate : null,
            },
            _ => d,
        };
    }

    private static IReadOnlyList<CustomField> MaskFields(IReadOnlyList<CustomField> fields) =>
        fields.Select(f => f.IsSecret ? f with { Value = string.Empty } : f).ToArray();

    public CipherEditorDraft GetDraft(string id) => DraftFor(_details.First(d => d.Id == id), FolderIdFor(id));

    public IReadOnlyList<FilterNode> GetFilters()
    {
        var items = GetItems();
        int CountType(VaultItemKind k) => items.Count(i => i.Kind == k && !i.IsDeleted && !i.IsArchived);
        var filters = new List<FilterNode>
        {
            new() { Label = "所有项目", Glyph = GlyphAllItems, Kind = FilterKind.AllItems, Count = items.Count(i => !i.IsDeleted && !i.IsArchived) },
            new() { Label = "收藏", Glyph = GlyphFavorite, Kind = FilterKind.Favorites, Count = items.Count(i => i.Favorite && !i.IsDeleted && !i.IsArchived) },
            new() { Label = "归档", Glyph = GlyphArchive, Kind = FilterKind.Archive, Count = items.Count(i => i.IsArchived && !i.IsDeleted) },
            new() { Label = "回收站", Glyph = GlyphTrash, Kind = FilterKind.Trash, Count = items.Count(i => i.IsDeleted) },
            new() { Label = "登录", Glyph = GlyphLogin, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Login, Count = CountType(VaultItemKind.Login) },
            new() { Label = "银行卡", Glyph = GlyphCard, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Card, Count = CountType(VaultItemKind.Card) },
            new() { Label = "身份", Glyph = GlyphIdentity, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Identity, Count = CountType(VaultItemKind.Identity) },
            new() { Label = "笔记", Glyph = GlyphNote, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Note, Count = CountType(VaultItemKind.Note) },
            new() { Label = "SSH 密钥", Glyph = GlyphSsh, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Ssh, Count = CountType(VaultItemKind.Ssh) },
            new() { Label = "银行账户", Glyph = GlyphBankAccount, Kind = FilterKind.Type, TypeFilter = VaultItemKind.BankAccount, Count = CountType(VaultItemKind.BankAccount) },
            new() { Label = "驾驶证", Glyph = GlyphDriversLicense, Kind = FilterKind.Type, TypeFilter = VaultItemKind.DriversLicense, Count = CountType(VaultItemKind.DriversLicense) },
            new() { Label = "护照", Glyph = GlyphPassport, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Passport, Count = CountType(VaultItemKind.Passport) },
        };
        filters.AddRange(_folders.Select(folder => new FilterNode
        {
            Label = folder.Name,
            Glyph = GlyphFolder,
            Kind = FilterKind.Folder,
            FolderId = folder.Id,
            Count = items.Count(i => i.FolderId == folder.Id && !i.IsDeleted && !i.IsArchived),
        }));
        return filters;
    }

    public Task<string> SaveCipherAsync(CipherEditorDraft draft, string? editingId, CancellationToken ct = default)
    {
        var folderId = string.IsNullOrWhiteSpace(draft.FolderId) ? null : draft.FolderId;
        var folderName = _folders.FirstOrDefault(f => f.Id == folderId).Name;

        if (string.IsNullOrEmpty(editingId))
        {
            var id = $"mock-{_nextId++}";
            _details.Add(RenderDetail(draft, id, folderName));
            _folderIds[id] = folderId;
            return Task.FromResult(id);
        }

        var index = _details.FindIndex(d => d.Id == editingId);
        if (index >= 0)
        {
            _details[index] = RenderDetail(draft, editingId, folderName);
            _folderIds[editingId] = folderId;
            _favoriteOverrides.Remove(editingId);
        }
        return Task.FromResult(editingId);
    }

    public Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default)
    {
        if (permanent)
        {
            _details.RemoveAll(d => d.Id == id);
            _deleted.Remove(id);
            _archived.Remove(id);
            _favoriteOverrides.Remove(id);
            _folderIds.Remove(id);
        }
        else
        {
            _deleted.Add(id);
        }
        return Task.CompletedTask;
    }

    public Task RestoreCipherAsync(string id, CancellationToken ct = default)
    {
        _deleted.Remove(id);
        return Task.CompletedTask;
    }

    public Task SetFavoriteAsync(string id, bool favorite, CancellationToken ct = default)
    {
        _favoriteOverrides[id] = favorite;
        return Task.CompletedTask;
    }

    public Task ArchiveCipherAsync(string id, CancellationToken ct = default)
    {
        _archived.Add(id);
        return Task.CompletedTask;
    }

    public Task UnarchiveCipherAsync(string id, CancellationToken ct = default)
    {
        _archived.Remove(id);
        return Task.CompletedTask;
    }

    public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(folderId))
        {
            _folders.Add(($"folder-{_nextId++}", name));
        }
        else
        {
            var index = _folders.FindIndex(f => f.Id == folderId);
            if (index >= 0)
                _folders[index] = (folderId, name);
        }
        return Task.CompletedTask;
    }

    public Task DeleteFolderAsync(string folderId, CancellationToken ct = default)
    {
        _folders.RemoveAll(f => f.Id == folderId);
        foreach (var key in _folderIds.Where(kv => kv.Value == folderId).Select(kv => kv.Key).ToList())
            _folderIds[key] = null;
        return Task.CompletedTask;
    }

    public Task SyncAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default)
    {
        var target = string.IsNullOrWhiteSpace(folderId) ? null : folderId;
        foreach (var id in ids)
        {
            if (_details.Any(d => d.Id == id))
                _folderIds[id] = target;
        }
        return Task.CompletedTask;
    }

    public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default)
    {
        foreach (var id in ids)
        {
            if (permanent)
            {
                _details.RemoveAll(d => d.Id == id);
                _deleted.Remove(id);
                _archived.Remove(id);
                _favoriteOverrides.Remove(id);
                _folderIds.Remove(id);
            }
            else
            {
                _deleted.Add(id);
            }
        }
        return Task.CompletedTask;
    }

    public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        foreach (var id in ids)
            _deleted.Remove(id);
        return Task.CompletedTask;
    }

    public Task ArchiveCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        foreach (var id in ids)
            _archived.Add(id);
        return Task.CompletedTask;
    }

    public Task UnarchiveCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        foreach (var id in ids)
            _archived.Remove(id);
        return Task.CompletedTask;
    }

    public Task TransferCiphersToOrganizationAsync(
        IReadOnlyCollection<CipherPlacementBaseline> baselines,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException(
            "Organization transfer is unavailable in the demo vault."));

    public Task BulkUpdateCipherCollectionsAsync(
        IReadOnlyCollection<CipherPlacementBaseline> baselines,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        bool removeCollections,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException(
            "Collection assignment is unavailable in the demo vault."));

    private string? FolderIdFor(string id) => _folderIds.TryGetValue(id, out var f) ? f : null;

    private static string SubtitleFor(CipherDetail d) => d switch
    {
        LoginDetail l => l.Username ?? "",
        CardDetail c => c.Brand ?? "银行卡",
        IdentityDetail => "身份",
        NoteDetail => "笔记",
        SshDetail => "SSH 密钥",
        BankAccountDetail bank => bank.BankName ?? "银行账户",
        DriversLicenseDetail license => DocumentSubtitle(
            "驾驶证",
            license.FullName,
            JoinSubtitleParts(", ", license.IssuingState, license.IssuingCountry)),
        PassportDetail passport => DocumentSubtitle(
            "护照",
            passport.FullName,
            passport.IssuingCountry),
        _ => "",
    };

    private static string DocumentSubtitle(string fallback, string? name, string? issuingLocation) =>
        JoinSubtitleParts(" · ", name, issuingLocation) ?? fallback;

    private static string? JoinSubtitleParts(string separator, params string?[] values)
    {
        var nonEmpty = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return nonEmpty.Length == 0 ? null : string.Join(separator, nonEmpty);
    }

    private static string GlyphFor(VaultItemKind k) => k switch
    {
        VaultItemKind.Login => GlyphLogin,
        VaultItemKind.Card => GlyphCard,
        VaultItemKind.Identity => GlyphIdentity,
        VaultItemKind.Note => GlyphNote,
        VaultItemKind.Ssh => GlyphSsh,
        VaultItemKind.BankAccount => GlyphBankAccount,
        VaultItemKind.DriversLicense => GlyphDriversLicense,
        VaultItemKind.Passport => GlyphPassport,
        _ => GlyphLogin,
    };

    // draft → 展示态 CipherDetail(mock 直接渲染,替代真实管线 加密→API→re-sync→快照→投影)。
    private static CipherDetail RenderDetail(CipherEditorDraft draft, string id, string? folderName)
    {
        var now = DateTimeOffset.Now;
        var customFields = draft.CustomFields
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .Select(f => new CustomField(
                f.Name.Trim(),
                f.Type == CipherEditorFieldType.Boolean ? f.BooleanValue.ToString() : f.Value,
                f.Type))
            .ToArray();

        return draft.Type switch
        {
            VaultItemKind.Login => new LoginDetail
            {
                Id = id, Name = draft.Name.Trim(), FolderName = folderName,
                Username = EmptyToNull(draft.Login.Username),
                Password = EmptyToNull(draft.Login.Password),
                TotpSecret = EmptyToNull(draft.Login.Totp),
                Uri = EmptyToNull(draft.Login.Uris.FirstOrDefault()?.Uri),
                Notes = EmptyToNull(draft.Notes), CustomFields = customFields,
                Created = now, Edited = now, Favorite = draft.Favorite, Reprompt = draft.Reprompt,
            },
            VaultItemKind.Card => new CardDetail
            {
                Id = id, Name = draft.Name.Trim(), FolderName = folderName,
                Cardholder = EmptyToNull(draft.Card.CardholderName),
                Number = EmptyToNull(draft.Card.Number),
                Expiry = FormatExpiry(draft.Card.ExpMonth, draft.Card.ExpYear),
                Brand = EmptyToNull(draft.Card.Brand),
                Cvv = EmptyToNull(draft.Card.Code),
                Notes = EmptyToNull(draft.Notes), CustomFields = customFields,
                Created = now, Edited = now, Favorite = draft.Favorite, Reprompt = draft.Reprompt,
            },
            VaultItemKind.Identity => new IdentityDetail
            {
                Id = id, Name = draft.Name.Trim(), FolderName = folderName,
                FullName = EmptyToNull(JoinNonEmpty(draft.Identity.FirstName, draft.Identity.MiddleName, draft.Identity.LastName)),
                Username = EmptyToNull(draft.Identity.Username),
                Company = EmptyToNull(draft.Identity.Company),
                Email = EmptyToNull(draft.Identity.Email),
                Phone = EmptyToNull(draft.Identity.Phone),
                IdNumber = EmptyToNull(JoinNonEmpty(draft.Identity.Ssn, draft.Identity.PassportNumber, draft.Identity.LicenseNumber)),
                Address = EmptyToNull(JoinNonEmpty(draft.Identity.Address1, draft.Identity.Address2, draft.Identity.Address3, draft.Identity.City, draft.Identity.State, draft.Identity.PostalCode, draft.Identity.Country)),
                Notes = EmptyToNull(draft.Notes), CustomFields = customFields,
                Created = now, Edited = now, Favorite = draft.Favorite, Reprompt = draft.Reprompt,
            },
            VaultItemKind.Note => new NoteDetail
            {
                Id = id, Name = draft.Name.Trim(), FolderName = folderName,
                Content = EmptyToNull(draft.Notes), Notes = EmptyToNull(draft.Notes), CustomFields = customFields,
                Created = now, Edited = now, Favorite = draft.Favorite, Reprompt = draft.Reprompt,
            },
            VaultItemKind.Ssh => new SshDetail
            {
                Id = id, Name = draft.Name.Trim(), FolderName = folderName,
                PrivateKey = EmptyToNull(draft.SshKey.PrivateKey),
                PublicKey = EmptyToNull(draft.SshKey.PublicKey),
                Fingerprint = EmptyToNull(draft.SshKey.KeyFingerprint),
                Notes = EmptyToNull(draft.Notes), CustomFields = customFields,
                Created = now, Edited = now, Favorite = draft.Favorite, Reprompt = draft.Reprompt,
            },
            VaultItemKind.BankAccount => new BankAccountDetail
            {
                Id = id, Name = draft.Name.Trim(), FolderName = folderName,
                BankName = EmptyToNull(draft.BankAccount.BankName),
                NameOnAccount = EmptyToNull(draft.BankAccount.NameOnAccount),
                AccountType = EmptyToNull(draft.BankAccount.AccountType),
                AccountNumber = EmptyToNull(draft.BankAccount.AccountNumber),
                RoutingNumber = EmptyToNull(draft.BankAccount.RoutingNumber),
                BranchNumber = EmptyToNull(draft.BankAccount.BranchNumber),
                Pin = EmptyToNull(draft.BankAccount.Pin),
                SwiftCode = EmptyToNull(draft.BankAccount.SwiftCode),
                Iban = EmptyToNull(draft.BankAccount.Iban),
                BankContactPhone = EmptyToNull(draft.BankAccount.BankContactPhone),
                Notes = EmptyToNull(draft.Notes), CustomFields = customFields,
                Created = now, Edited = now, Favorite = draft.Favorite, Reprompt = draft.Reprompt,
            },
            VaultItemKind.DriversLicense => new DriversLicenseDetail
            {
                Id = id, Name = draft.Name.Trim(), FolderName = folderName,
                FullName = EmptyToNull(JoinNonEmpty(draft.DriversLicense.FirstName, draft.DriversLicense.MiddleName, draft.DriversLicense.LastName)),
                DateOfBirth = EmptyToNull(draft.DriversLicense.DateOfBirth),
                LicenseNumber = EmptyToNull(draft.DriversLicense.LicenseNumber),
                IssuingCountry = EmptyToNull(draft.DriversLicense.IssuingCountry),
                IssuingState = EmptyToNull(draft.DriversLicense.IssuingState),
                IssueDate = EmptyToNull(draft.DriversLicense.IssueDate),
                ExpirationDate = EmptyToNull(draft.DriversLicense.ExpirationDate),
                IssuingAuthority = EmptyToNull(draft.DriversLicense.IssuingAuthority),
                LicenseClass = EmptyToNull(draft.DriversLicense.LicenseClass),
                Notes = EmptyToNull(draft.Notes), CustomFields = customFields,
                Created = now, Edited = now, Favorite = draft.Favorite, Reprompt = draft.Reprompt,
            },
            VaultItemKind.Passport => new PassportDetail
            {
                Id = id, Name = draft.Name.Trim(), FolderName = folderName,
                FullName = EmptyToNull(JoinNonEmpty(draft.Passport.GivenName, draft.Passport.Surname)),
                DateOfBirth = EmptyToNull(draft.Passport.DateOfBirth),
                Sex = EmptyToNull(draft.Passport.Sex),
                BirthPlace = EmptyToNull(draft.Passport.BirthPlace),
                Nationality = EmptyToNull(draft.Passport.Nationality),
                IssuingCountry = EmptyToNull(draft.Passport.IssuingCountry),
                PassportNumber = EmptyToNull(draft.Passport.PassportNumber),
                PassportType = EmptyToNull(draft.Passport.PassportType),
                NationalIdentificationNumber = EmptyToNull(draft.Passport.NationalIdentificationNumber),
                IssuingAuthority = EmptyToNull(draft.Passport.IssuingAuthority),
                IssueDate = EmptyToNull(draft.Passport.IssueDate),
                ExpirationDate = EmptyToNull(draft.Passport.ExpirationDate),
                Notes = EmptyToNull(draft.Notes), CustomFields = customFields,
                Created = now, Edited = now, Favorite = draft.Favorite, Reprompt = draft.Reprompt,
            },
            _ => throw new InvalidOperationException($"Unsupported cipher type: {draft.Type}"),
        };
    }

    // 展示态 CipherDetail → draft(编辑载入)。mock 专用,反向于 RenderDetail。
    private static CipherEditorDraft DraftFor(CipherDetail detail, string? folderId)
    {
        var draft = new CipherEditorDraft
        {
            Type = detail.Kind,
            Name = detail.Name,
            FolderId = folderId,
            Favorite = detail.Favorite,
            Reprompt = detail.Reprompt,
            Notes = detail.Notes ?? string.Empty,
        };

        switch (detail)
        {
            case LoginDetail login:
                draft.Login.Username = login.Username ?? string.Empty;
                draft.Login.Password = login.Password ?? string.Empty;
                draft.Login.Totp = login.TotpSecret ?? string.Empty;
                draft.Login.Uris[0].Uri = login.Uri ?? string.Empty;
                break;
            case CardDetail card:
                draft.Card.CardholderName = card.Cardholder ?? string.Empty;
                draft.Card.Number = card.Number ?? string.Empty;
                draft.Card.Brand = card.Brand ?? string.Empty;
                draft.Card.Code = card.Cvv ?? string.Empty;
                break;
            case IdentityDetail identity:
                draft.Identity.FirstName = identity.FullName ?? string.Empty;
                draft.Identity.Username = identity.Username ?? string.Empty;
                draft.Identity.Company = identity.Company ?? string.Empty;
                draft.Identity.Email = identity.Email ?? string.Empty;
                draft.Identity.Phone = identity.Phone ?? string.Empty;
                draft.Identity.Address1 = identity.Address ?? string.Empty;
                break;
            case NoteDetail note:
                draft.Notes = note.Content ?? draft.Notes;
                break;
            case SshDetail ssh:
                draft.SshKey.PrivateKey = ssh.PrivateKey ?? string.Empty;
                draft.SshKey.PublicKey = ssh.PublicKey ?? string.Empty;
                draft.SshKey.KeyFingerprint = ssh.Fingerprint ?? string.Empty;
                break;
            case BankAccountDetail bank:
                draft.BankAccount.BankName = bank.BankName ?? string.Empty;
                draft.BankAccount.NameOnAccount = bank.NameOnAccount ?? string.Empty;
                draft.BankAccount.AccountType = bank.AccountType ?? string.Empty;
                draft.BankAccount.AccountNumber = bank.AccountNumber ?? string.Empty;
                draft.BankAccount.RoutingNumber = bank.RoutingNumber ?? string.Empty;
                draft.BankAccount.BranchNumber = bank.BranchNumber ?? string.Empty;
                draft.BankAccount.Pin = bank.Pin ?? string.Empty;
                draft.BankAccount.SwiftCode = bank.SwiftCode ?? string.Empty;
                draft.BankAccount.Iban = bank.Iban ?? string.Empty;
                draft.BankAccount.BankContactPhone = bank.BankContactPhone ?? string.Empty;
                break;
            case DriversLicenseDetail license:
                draft.DriversLicense.FirstName = license.FullName ?? string.Empty;
                draft.DriversLicense.DateOfBirth = license.DateOfBirth ?? string.Empty;
                draft.DriversLicense.LicenseNumber = license.LicenseNumber ?? string.Empty;
                draft.DriversLicense.IssuingCountry = license.IssuingCountry ?? string.Empty;
                draft.DriversLicense.IssuingState = license.IssuingState ?? string.Empty;
                draft.DriversLicense.IssueDate = license.IssueDate ?? string.Empty;
                draft.DriversLicense.ExpirationDate = license.ExpirationDate ?? string.Empty;
                draft.DriversLicense.IssuingAuthority = license.IssuingAuthority ?? string.Empty;
                draft.DriversLicense.LicenseClass = license.LicenseClass ?? string.Empty;
                break;
            case PassportDetail passport:
                draft.Passport.GivenName = passport.FullName ?? string.Empty;
                draft.Passport.DateOfBirth = passport.DateOfBirth ?? string.Empty;
                draft.Passport.Sex = passport.Sex ?? string.Empty;
                draft.Passport.BirthPlace = passport.BirthPlace ?? string.Empty;
                draft.Passport.Nationality = passport.Nationality ?? string.Empty;
                draft.Passport.IssuingCountry = passport.IssuingCountry ?? string.Empty;
                draft.Passport.PassportNumber = passport.PassportNumber ?? string.Empty;
                draft.Passport.PassportType = passport.PassportType ?? string.Empty;
                draft.Passport.NationalIdentificationNumber = passport.NationalIdentificationNumber ?? string.Empty;
                draft.Passport.IssuingAuthority = passport.IssuingAuthority ?? string.Empty;
                draft.Passport.IssueDate = passport.IssueDate ?? string.Empty;
                draft.Passport.ExpirationDate = passport.ExpirationDate ?? string.Empty;
                break;
        }

        foreach (var field in detail.CustomFields)
        {
            draft.CustomFields.Add(new CustomFieldEditorDraft
            {
                Name = field.Label,
                Type = field.Type,
                Value = field.Type == CipherEditorFieldType.Boolean ? string.Empty : field.Value,
                BooleanValue = field.Type == CipherEditorFieldType.Boolean
                    && string.Equals(field.Value, bool.TrueString, StringComparison.OrdinalIgnoreCase),
            });
        }

        return draft;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FormatExpiry(string month, string year)
    {
        var m = EmptyToNull(month);
        var y = EmptyToNull(year);
        return (m, y) switch
        {
            (null, null) => null,
            (not null, null) => m,
            (null, not null) => y,
            _ => $"{m}/{y}",
        };
    }

    private static string JoinNonEmpty(params string[] values) =>
        string.Join(" ", values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));

    private static List<CipherDetail> BuildDetails() => new()
    {
        new LoginDetail
        {
            Id = "1", Name = "百度网盘", FolderName = "文件夹1", Favorite = true,
            Username = "admin", Password = "p@ssw0rd123", TotpSecret = "506999", Uri = "https://www.baidu.com",
            Notes = "这是一个备注",
            CustomFields = new[] { new CustomField("字段测试", "测试成功") },
            Created = new DateTimeOffset(2026, 2, 26, 1, 59, 48, TimeSpan.Zero),
            Edited = new DateTimeOffset(2026, 3, 2, 0, 42, 30, TimeSpan.Zero),
        },
        new CardDetail
        {
            Id = "2", Name = "招商银行", Cardholder = "张三",
            Number = "6225880212341234", Expiry = "08/28", Brand = "Visa", Cvv = "123",
            Created = new DateTimeOffset(2026, 2, 20, 10, 0, 0, TimeSpan.Zero),
            Edited = new DateTimeOffset(2026, 2, 20, 10, 0, 0, TimeSpan.Zero),
        },
        new IdentityDetail
        {
            Id = "3", Name = "个人身份", FullName = "张三", Email = "zhang@example.com",
            Phone = "13800001234", IdNumber = "110101199001011234", Address = "北京市朝阳区",
            Created = new DateTimeOffset(2026, 2, 18, 9, 0, 0, TimeSpan.Zero),
            Edited = new DateTimeOffset(2026, 2, 18, 9, 0, 0, TimeSpan.Zero),
        },
        new NoteDetail
        {
            Id = "4", Name = "服务器密钥备份", Content = "这是一段安全笔记的多行内容。\n第二行。",
            Created = new DateTimeOffset(2026, 2, 15, 8, 0, 0, TimeSpan.Zero),
            Edited = new DateTimeOffset(2026, 2, 15, 8, 0, 0, TimeSpan.Zero),
        },
        new SshDetail
        {
            Id = "5", Name = "生产服务器 key",
            PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5...",
            PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\n...",
            Fingerprint = "SHA256:abcd1234efgh5678",
            Created = new DateTimeOffset(2026, 2, 10, 7, 0, 0, TimeSpan.Zero),
            Edited = new DateTimeOffset(2026, 2, 10, 7, 0, 0, TimeSpan.Zero),
        },
        new LoginDetail
        {
            Id = "6", Name = "已删除的旧账号",
            Username = "old@example.com", Password = "obsolete",
            Created = new DateTimeOffset(2026, 1, 5, 6, 0, 0, TimeSpan.Zero),
            Edited = new DateTimeOffset(2026, 1, 10, 6, 0, 0, TimeSpan.Zero),
        },
    };
}
