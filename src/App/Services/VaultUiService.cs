using App.ViewModels.Models;
using Core.Enums;
using Core.Models;
using Core.Services;
using Vault;

namespace App.Services;

// 演示/离线会话(VaultSession.IsDemo)特判:该会话未配置服务端地址,任何走网络的写入/同步都会抛
// 「An invalid request URI was provided…」。故 IsDemo 时全部改走会话内存中的离线数据(建/改/删/移动/同步),
// 完全不触网。session 为 null(设计时/旧构造)时按在线路径处理。
public sealed class VaultUiService : IVaultUiService
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
    private const string GlyphOrganization = "\uE716";
    private const string GlyphCollection = "\uE8B7";
    private const string GlyphBankAccount = "\uE825";
    private const string GlyphDriversLicense = "\uE77B";
    private const string GlyphPassport = "\uE8A5";

    private readonly IVaultService _vault;
    private readonly IVaultWriteService _writeService;
    private readonly ISyncService _sync;
    private readonly VaultSession? _session;

    public event EventHandler? CipherCreateGateChanged
    {
        add => _writeService.CipherCreateGateChanged += value;
        remove => _writeService.CipherCreateGateChanged -= value;
    }

    public VaultUiService(IVaultService vault, IVaultWriteService writeService, ISyncService sync, VaultSession? session = null)
    {
        _vault = vault;
        _writeService = writeService;
        _sync = sync;
        _session = session;
    }

    private bool IsDemo => _session?.IsDemo == true;
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private Cipher Find(string id) => _vault.GetCiphers().First(c => c.Id == id);

    public int SkippedCipherCount => _vault.Snapshot.SkippedCipherCount;
    public int UnsupportedOrganizationCipherCount =>
        _vault.Snapshot.UnsupportedOrganizationCipherCount;
    public bool DataStale => _vault.Snapshot.DataStale;
    public VaultCapabilities Capabilities => _vault.Snapshot.Capabilities;

    public IReadOnlyList<CipherListItem> GetItems()
    {
        var capabilities = Capabilities;
        return _vault.GetCiphers()
            .Select(cipher => ToListItem(cipher, capabilities))
            .ToList();
    }

    public IReadOnlyList<CipherDestinationOption> GetCipherDestinations()
    {
        var destinations = new List<CipherDestinationOption>();
        if (!VaultPolicyEvaluator.IsEnabledForUser(
                _vault.Snapshot,
                OrganizationPolicyType.PersonalOwnership))
        {
            destinations.Add(CipherDestinationOption.Personal);
        }

        foreach (var organization in _vault.GetOrganizations()
            .Where(organization => organization.IsMember
                && organization.Enabled
                && organization.Status == 2
                && !string.IsNullOrWhiteSpace(organization.EncryptedKey)
                && _vault.GetCollections().Any(collection =>
                    collection.OrganizationId == organization.Id && !collection.ReadOnly)))
        {
            destinations.Add(new CipherDestinationOption(
                $"{organization.Name}（组织）",
                organization.Id,
                null));
        }

        return destinations;
    }

    public IReadOnlyList<CipherCollectionOption> GetCipherCollectionOptions(
        string organizationId,
        IReadOnlyCollection<string>? selectedCollectionIds = null,
        bool lockReadOnlyAssignments = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        var selected = (selectedCollectionIds ?? Array.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);
        return _vault.GetCollections()
            .Where(collection =>
                string.Equals(collection.OrganizationId, organizationId, StringComparison.Ordinal)
                && (!collection.ReadOnly || selected.Contains(collection.Id)))
            .OrderBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(collection => new CipherCollectionOption
            {
                Id = collection.Id,
                OrganizationId = collection.OrganizationId,
                Name = collection.Name,
                IsSelected = selected.Contains(collection.Id),
                IsLocked = lockReadOnlyAssignments
                    && collection.ReadOnly
                    && selected.Contains(collection.Id),
            })
            .ToArray();
    }

    public CipherPlacementBaseline GetCipherPlacementBaseline(string id) =>
        CipherPlacementBaseline.Capture(Find(id));

    public CipherAccessPolicy GetAccessPolicy(string id)
    {
        var cipher = Find(id);
        return new CipherAccessPolicy(cipher.Id, cipher.Reprompt);
    }

    // includeSensitive==false 且条目 Reprompt==true 时返回脱敏投影;Reprompt==false 的条目恒返回完整投影。
    public CipherDetail GetDetail(string id, bool includeSensitive)
    {
        var cipher = Find(id);
        return ToDetail(cipher, includeSensitive || !cipher.Reprompt);
    }

    public CipherEditorDraft GetDraft(string id) => CipherDraftMapper.ToDraft(Find(id));

    public async Task<string> SaveCipherAsync(CipherEditorDraft draft, string? editingId, CancellationToken ct = default)
    {
        var original = string.IsNullOrEmpty(editingId)
            ? null
            : _vault.GetCiphers().FirstOrDefault(c => c.Id == editingId);
        if (!string.IsNullOrEmpty(editingId) && original is null)
        {
            throw new CipherEditConflictException(
                "The cipher is no longer present. Reload the vault before saving.");
        }
        var cipher = CipherDraftMapper.ToCipher(draft, original);

        if (IsDemo)
        {
            cipher = original is null
                ? cipher.WithMetadata(Guid.NewGuid().ToString("N"), Now, Now)
                : cipher.WithMetadata(cipher.Id, cipher.CreationDate, Now);
            _session!.UpsertCipher(cipher);
            return cipher.Id;
        }

        if (original is null)
        {
            return await _writeService.SaveCipherAndGetIdAsync(cipher, ct)
                ?? throw new InvalidDataException(
                    "The vault writer completed a cipher create without returning its identifier.");
        }

        await _writeService.SaveCipherAsync(cipher, ct);
        return editingId!;
    }

    public CipherCreatePending? GetPendingCipherCreate() =>
        _writeService.GetPendingCipherCreate();

    public CipherCreatePending RememberPendingCipherCreate(CipherCreatePending pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(pending.Witness);
        var remembered = _writeService.GetPendingCipherCreate();
        return remembered is not null
            && string.Equals(
                remembered.Witness.AccountId,
                pending.Witness.AccountId,
                StringComparison.Ordinal)
            && remembered.Witness.Endpoints == pending.Witness.Endpoints
            && string.Equals(
                remembered.Witness.KeyFingerprint,
                pending.Witness.KeyFingerprint,
                StringComparison.Ordinal)
            ? remembered
            : pending;
    }

    public bool HasPendingOrInFlightCipherCreate() =>
        _writeService.HasPendingOrInFlightCipherCreate();

    public bool AbandonPendingCipherCreate(CipherCreatePending expected) =>
        _writeService.AbandonPendingCipherCreate(expected);

    public async Task<CipherCreateReconciliationResult> SyncAndReconcileCipherCreateAsync(
        CipherCreatePending pending,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        return await _writeService.SyncAndReconcileCipherCreateAsync(pending, ct);
    }

    public Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            if (permanent)
                _session!.RemoveCipher(id);
            else
                _session!.UpsertCipher(Find(id).WithDeletedDate(Now));
            return Task.CompletedTask;
        }

        return _writeService.DeleteCipherAsync(id, permanent, ct);
    }

    public Task RestoreCipherAsync(string id, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            _session!.UpsertCipher(Find(id).WithDeletedDate(null));
            return Task.CompletedTask;
        }

        return _writeService.RestoreCipherAsync(id, ct);
    }

    public Task SetFavoriteAsync(string id, bool favorite, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            _session!.UpsertCipher(Find(id).WithFavorite(favorite));
            return Task.CompletedTask;
        }

        return _writeService.SetCipherFavoriteAsync(id, favorite, ct);
    }

    public Task ArchiveCipherAsync(string id, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            _session!.UpsertCipher(Find(id).WithArchivedDate(Now));
            return Task.CompletedTask;
        }

        return _writeService.ArchiveCipherAsync(id, ct);
    }

    public Task UnarchiveCipherAsync(string id, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            _session!.UpsertCipher(Find(id).WithArchivedDate(null));
            return Task.CompletedTask;
        }

        return _writeService.UnarchiveCipherAsync(id, ct);
    }

    public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            _session!.UpsertFolder(new Folder
            {
                Id = string.IsNullOrEmpty(folderId) ? Guid.NewGuid().ToString("N") : folderId,
                Name = name,
                RevisionDate = Now,
            });
            return Task.CompletedTask;
        }

        return _writeService.SaveFolderAsync(folderId, name, ct);
    }

    public Task DeleteFolderAsync(string folderId, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            _session!.RemoveFolder(folderId);
            _session!.ClearFolderReference(folderId);
            return Task.CompletedTask;
        }

        return _writeService.DeleteFolderAsync(folderId, ct);
    }

    public Task SyncAsync(CancellationToken ct = default)
    {
        if (IsDemo)
            return Task.CompletedTask;

        return _sync.SyncAsync(ct);
    }

    public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            foreach (var id in ids)
                _session!.UpsertCipher(Find(id).WithFolderId(folderId));
            return Task.CompletedTask;
        }

        return _writeService.MoveCiphersAsync(ids, folderId, ct);
    }

    public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            foreach (var id in ids)
            {
                if (permanent)
                    _session!.RemoveCipher(id);
                else
                    _session!.UpsertCipher(Find(id).WithDeletedDate(Now));
            }
            return Task.CompletedTask;
        }

        return _writeService.DeleteCiphersAsync(ids, permanent, ct);
    }

    public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            foreach (var id in ids)
                _session!.UpsertCipher(Find(id).WithDeletedDate(null));
            return Task.CompletedTask;
        }

        return _writeService.RestoreCiphersAsync(ids, ct);
    }

    public Task TransferCipherToOrganizationAsync(
        CipherPlacementBaseline baseline,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default)
    {
        if (IsDemo)
            throw new NotSupportedException("Organization transfer is unavailable in the demo vault.");
        return _writeService.TransferCipherToOrganizationAsync(
            baseline,
            organizationId,
            collectionIds,
            ct);
    }

    public Task SetCipherCollectionsAsync(
        CipherPlacementBaseline baseline,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default)
    {
        if (IsDemo)
            throw new NotSupportedException("Collection assignment is unavailable in the demo vault.");
        return _writeService.SetCipherCollectionsAsync(baseline, collectionIds, ct);
    }

    public Task TransferCiphersToOrganizationAsync(
        IReadOnlyCollection<CipherPlacementBaseline> baselines,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default)
    {
        if (IsDemo)
            throw new NotSupportedException("Organization transfer is unavailable in the demo vault.");
        return _writeService.TransferCiphersToOrganizationAsync(
            baselines,
            organizationId,
            collectionIds,
            ct);
    }

    public Task BulkUpdateCipherCollectionsAsync(
        IReadOnlyCollection<CipherPlacementBaseline> baselines,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        bool removeCollections,
        CancellationToken ct = default)
    {
        if (IsDemo)
            throw new NotSupportedException("Collection assignment is unavailable in the demo vault.");
        return _writeService.BulkUpdateCipherCollectionsAsync(
            baselines,
            organizationId,
            collectionIds,
            removeCollections,
            ct);
    }

    public Task ArchiveCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            foreach (var id in ids)
                _session!.UpsertCipher(Find(id).WithArchivedDate(Now));
            return Task.CompletedTask;
        }

        return _writeService.ArchiveCiphersAsync(ids, ct);
    }

    public Task UnarchiveCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            foreach (var id in ids)
                _session!.UpsertCipher(Find(id).WithArchivedDate(null));
            return Task.CompletedTask;
        }

        return _writeService.UnarchiveCiphersAsync(ids, ct);
    }

    public IReadOnlyList<FilterNode> GetFilters()
    {
        var items = GetItems();
        var memberOrganizationIds = _vault.GetOrganizations()
            .Where(organization => organization.IsMember
                && organization.Enabled
                && organization.Status == 2)
            .Select(organization => organization.Id)
            .ToHashSet(StringComparer.Ordinal);
        int CountType(VaultItemKind kind) => items.Count(i => i.Kind == kind && !i.IsDeleted && !i.IsArchived);

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

        filters.AddRange(_vault.GetOrganizations()
            .Where(organization => memberOrganizationIds.Contains(organization.Id))
            .Select(organization => new FilterNode
            {
                Label = organization.Name,
                Glyph = GlyphOrganization,
                Kind = FilterKind.Organization,
                OrganizationId = organization.Id,
                Count = items.Count(item =>
                    item.OrganizationId == organization.Id && !item.IsDeleted && !item.IsArchived),
            }));

        filters.AddRange(_vault.GetCollections()
            .Where(collection => memberOrganizationIds.Contains(collection.OrganizationId))
            .Select(collection => new FilterNode
            {
                Label = collection.Name,
                Glyph = GlyphCollection,
                Kind = FilterKind.Collection,
                OrganizationId = collection.OrganizationId,
                CollectionId = collection.Id,
                Count = items.Count(item =>
                    item.CollectionIds.Contains(collection.Id, StringComparer.Ordinal)
                    && !item.IsDeleted
                    && !item.IsArchived),
            }));

        filters.AddRange(_vault.GetFolders().Select(folder => new FilterNode
        {
            Label = folder.Name,
            Glyph = GlyphFolder,
            Kind = FilterKind.Folder,
            FolderId = folder.Id,
            Count = items.Count(item => item.FolderId == folder.Id && !item.IsDeleted && !item.IsArchived),
        }));

        return filters;
    }

    private CipherListItem ToListItem(Cipher cipher, VaultCapabilities capabilities)
    {
        return new()
        {
        Id = cipher.Id,
        Name = cipher.Name,
        Kind = KindFor(cipher.Type),
        Subtitle = SubtitleFor(cipher),
        Glyph = GlyphFor(KindFor(cipher.Type)),
        Favorite = cipher.Favorite,
        FolderId = cipher.FolderId,
        OrganizationId = cipher.OrganizationId,
        CollectionIds = cipher.CollectionIds,
        CanEdit = CanEditContent(cipher, capabilities),
        CanViewPassword = cipher.CanViewPassword,
        CanDelete = cipher.CanDelete,
        CanRestore = cipher.CanRestore,
        CanClone = CanClone(cipher, capabilities),
        CanTransferToOrganization = CanTransferToOrganization(cipher, capabilities),
        CanManageCollections = CanManageCollections(cipher),
        CanArchive = capabilities.CanArchive,
        CanUnarchive = capabilities.CanUnarchive,
        IsDeleted = cipher.IsDeleted,
        IsArchived = cipher.IsArchived,
        IconDomain = cipher.Type == CipherType.Login
            ? Core.IconDomain.Extract(cipher.Login?.Uris.FirstOrDefault()?.Uri)
            : null,
        SearchHaystack = Vault.VaultSearch.BuildHaystack(cipher),
        HasTotp = cipher.CanViewPassword && !string.IsNullOrEmpty(cipher.Login?.Totp),
        HasAttachment = cipher.Attachments.Count > 0,
        HasUri = cipher.Login?.Uris.Any(u => !string.IsNullOrEmpty(u.Uri)) == true,
        RevisionDate = cipher.RevisionDate,
        CreationDate = cipher.CreationDate,
        };
    }

    private bool CanClone(Cipher cipher, VaultCapabilities capabilities)
    {
        if (!cipher.CanViewPassword
            || !CipherWritePolicy.CanWrite(cipher.Type, capabilities))
            return false;

        if (string.IsNullOrWhiteSpace(cipher.OrganizationId))
            return true;

        return _vault.GetCollections().Any(collection =>
            collection.OrganizationId == cipher.OrganizationId
            && collection.Manage
            && cipher.CollectionIds.Contains(collection.Id, StringComparer.Ordinal));
    }

    private bool CanTransferToOrganization(Cipher cipher, VaultCapabilities capabilities) =>
        string.IsNullOrWhiteSpace(cipher.OrganizationId)
        && cipher.CanEdit
        && cipher.CanViewPassword
        && CipherWritePolicy.CanWrite(cipher.Type, capabilities)
        && (!string.IsNullOrWhiteSpace(cipher.Key) || cipher.Attachments.Count == 0)
        && HasWritableOrganizationDestination();

    private bool HasWritableOrganizationDestination()
    {
        try
        {
            return GetCipherDestinations().Any(destination => destination.OrganizationId is not null);
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private bool CanManageCollections(Cipher cipher)
    {
        if (string.IsNullOrWhiteSpace(cipher.OrganizationId))
            return false;
        if (cipher.CanEdit && cipher.CanViewPassword)
            return true;
        return _vault.GetCollections().Any(collection =>
            collection.Manage
            && cipher.CollectionIds.Contains(collection.Id, StringComparer.Ordinal));
    }

    // 详情投影。includeSensitive==false 时抹除全部秘密:密码/TOTP/密码历史/卡号/CVV/证件号/私钥/笔记内容/备注,
    // 且隐藏自定义字段值置空。SensitiveUnlocked 反映本投影是否已解锁。
    private CipherDetail ToDetail(Cipher cipher, bool includeSensitive)
    {
        includeSensitive = includeSensitive && cipher.CanViewPassword;
        var canEdit = CanEditContent(cipher, Capabilities);
        var commonFields = MapFields(cipher, includeSensitive);
        var folderName = FolderNameFor(cipher.FolderId);
        var attachments = MapAttachments(cipher.Attachments);
        var notes = includeSensitive ? cipher.Notes : null;

        return cipher.Type switch
        {
            CipherType.Login => new LoginDetail
            {
                Id = cipher.Id,
                Name = cipher.Name,
                FolderName = folderName,
                Notes = notes,
                CustomFields = commonFields,
                Created = cipher.CreationDate,
                Edited = cipher.RevisionDate,
                IsDeleted = cipher.IsDeleted,
                IsArchived = cipher.IsArchived,
                CanEdit = canEdit,
                CanDelete = cipher.CanDelete,
                CanRestore = cipher.CanRestore,
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt,
                SensitiveUnlocked = includeSensitive,
                Attachments = attachments,
                Username = cipher.Login?.Username,
                Password = includeSensitive ? cipher.Login?.Password : null,
                TotpSecret = includeSensitive ? cipher.Login?.Totp : null,
                Uri = cipher.Login?.Uris.FirstOrDefault()?.Uri,
                Passkeys = MapPasskeys(cipher.Login?.Fido2Credentials),
                PasswordHistory = includeSensitive
                    ? (cipher.PasswordHistory ?? Array.Empty<Core.Models.PasswordHistoryEntry>())
                        .Select(h => new PasswordHistoryItem(
                            h.Password,
                            h.LastUsedDate is { } d ? d.LocalDateTime.ToString("yyyy/M/d HH:mm:ss") : "—"))
                        .ToList()
                    : Array.Empty<PasswordHistoryItem>(),
            },
            CipherType.Card => new CardDetail
            {
                Id = cipher.Id,
                Name = cipher.Name,
                FolderName = folderName,
                Notes = notes,
                CustomFields = commonFields,
                Created = cipher.CreationDate,
                Edited = cipher.RevisionDate,
                IsDeleted = cipher.IsDeleted,
                IsArchived = cipher.IsArchived,
                CanEdit = canEdit,
                CanDelete = cipher.CanDelete,
                CanRestore = cipher.CanRestore,
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt,
                SensitiveUnlocked = includeSensitive,
                Attachments = attachments,
                Cardholder = cipher.Card?.CardholderName,
                Number = includeSensitive ? cipher.Card?.Number : null,
                Expiry = JoinExpiry(cipher.Card?.ExpMonth, cipher.Card?.ExpYear),
                Brand = cipher.Card?.Brand,
                Cvv = includeSensitive ? cipher.Card?.Code : null,
            },
            CipherType.Identity => new IdentityDetail
            {
                Id = cipher.Id,
                Name = cipher.Name,
                FolderName = folderName,
                Notes = notes,
                CustomFields = commonFields,
                Created = cipher.CreationDate,
                Edited = cipher.RevisionDate,
                IsDeleted = cipher.IsDeleted,
                IsArchived = cipher.IsArchived,
                CanEdit = canEdit,
                CanDelete = cipher.CanDelete,
                CanRestore = cipher.CanRestore,
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt,
                SensitiveUnlocked = includeSensitive,
                Attachments = attachments,
                FullName = JoinNonEmpty(" ", cipher.Identity?.FirstName, cipher.Identity?.MiddleName, cipher.Identity?.LastName),
                Username = cipher.Identity?.Username,
                Company = cipher.Identity?.Company,
                Email = cipher.Identity?.Email,
                Phone = cipher.Identity?.Phone,
                IdNumber = includeSensitive
                    ? cipher.Identity?.Ssn ?? cipher.Identity?.PassportNumber ?? cipher.Identity?.LicenseNumber
                    : null,
                Address = JoinNonEmpty(", ", cipher.Identity?.Address1, cipher.Identity?.Address2, cipher.Identity?.Address3,
                    cipher.Identity?.City, cipher.Identity?.State, cipher.Identity?.PostalCode, cipher.Identity?.Country),
            },
            CipherType.SecureNote => new NoteDetail
            {
                Id = cipher.Id,
                Name = cipher.Name,
                FolderName = folderName,
                Notes = notes,
                CustomFields = commonFields,
                Created = cipher.CreationDate,
                Edited = cipher.RevisionDate,
                IsDeleted = cipher.IsDeleted,
                IsArchived = cipher.IsArchived,
                CanEdit = canEdit,
                CanDelete = cipher.CanDelete,
                CanRestore = cipher.CanRestore,
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt,
                SensitiveUnlocked = includeSensitive,
                Attachments = attachments,
                Content = includeSensitive ? cipher.Notes : null,
            },
            CipherType.SshKey => new SshDetail
            {
                Id = cipher.Id,
                Name = cipher.Name,
                FolderName = folderName,
                Notes = notes,
                CustomFields = commonFields,
                Created = cipher.CreationDate,
                Edited = cipher.RevisionDate,
                IsDeleted = cipher.IsDeleted,
                IsArchived = cipher.IsArchived,
                CanEdit = canEdit,
                CanDelete = cipher.CanDelete,
                CanRestore = cipher.CanRestore,
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt,
                SensitiveUnlocked = includeSensitive,
                Attachments = attachments,
                PublicKey = cipher.Ssh?.PublicKey,
                PrivateKey = includeSensitive ? cipher.Ssh?.PrivateKey : null,
                Fingerprint = cipher.Ssh?.Fingerprint,
            },
            CipherType.BankAccount => new BankAccountDetail
            {
                Id = cipher.Id,
                Name = cipher.Name,
                FolderName = folderName,
                Notes = notes,
                CustomFields = commonFields,
                Created = cipher.CreationDate,
                Edited = cipher.RevisionDate,
                IsDeleted = cipher.IsDeleted,
                IsArchived = cipher.IsArchived,
                CanEdit = canEdit,
                CanDelete = cipher.CanDelete,
                CanRestore = cipher.CanRestore,
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt,
                SensitiveUnlocked = includeSensitive,
                Attachments = attachments,
                BankName = cipher.BankAccount?.BankName,
                NameOnAccount = cipher.BankAccount?.NameOnAccount,
                AccountType = cipher.BankAccount?.AccountType,
                AccountNumber = includeSensitive ? cipher.BankAccount?.AccountNumber : null,
                RoutingNumber = includeSensitive ? cipher.BankAccount?.RoutingNumber : null,
                BranchNumber = includeSensitive ? cipher.BankAccount?.BranchNumber : null,
                Pin = includeSensitive ? cipher.BankAccount?.Pin : null,
                SwiftCode = includeSensitive ? cipher.BankAccount?.SwiftCode : null,
                Iban = includeSensitive ? cipher.BankAccount?.Iban : null,
                BankContactPhone = cipher.BankAccount?.BankContactPhone,
            },
            CipherType.DriversLicense => new DriversLicenseDetail
            {
                Id = cipher.Id,
                Name = cipher.Name,
                FolderName = folderName,
                Notes = notes,
                CustomFields = commonFields,
                Created = cipher.CreationDate,
                Edited = cipher.RevisionDate,
                IsDeleted = cipher.IsDeleted,
                IsArchived = cipher.IsArchived,
                CanEdit = canEdit,
                CanDelete = cipher.CanDelete,
                CanRestore = cipher.CanRestore,
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt,
                SensitiveUnlocked = includeSensitive,
                Attachments = attachments,
                FullName = JoinNonEmpty(" ", cipher.DriversLicense?.FirstName, cipher.DriversLicense?.MiddleName, cipher.DriversLicense?.LastName),
                DateOfBirth = includeSensitive ? cipher.DriversLicense?.DateOfBirth : null,
                LicenseNumber = includeSensitive ? cipher.DriversLicense?.LicenseNumber : null,
                IssuingCountry = cipher.DriversLicense?.IssuingCountry,
                IssuingState = cipher.DriversLicense?.IssuingState,
                IssueDate = includeSensitive ? cipher.DriversLicense?.IssueDate : null,
                ExpirationDate = includeSensitive ? cipher.DriversLicense?.ExpirationDate : null,
                IssuingAuthority = cipher.DriversLicense?.IssuingAuthority,
                LicenseClass = cipher.DriversLicense?.LicenseClass,
            },
            CipherType.Passport => new PassportDetail
            {
                Id = cipher.Id,
                Name = cipher.Name,
                FolderName = folderName,
                Notes = notes,
                CustomFields = commonFields,
                Created = cipher.CreationDate,
                Edited = cipher.RevisionDate,
                IsDeleted = cipher.IsDeleted,
                IsArchived = cipher.IsArchived,
                CanEdit = canEdit,
                CanDelete = cipher.CanDelete,
                CanRestore = cipher.CanRestore,
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt,
                SensitiveUnlocked = includeSensitive,
                Attachments = attachments,
                FullName = JoinNonEmpty(" ", cipher.Passport?.GivenName, cipher.Passport?.Surname),
                DateOfBirth = includeSensitive ? cipher.Passport?.DateOfBirth : null,
                Sex = includeSensitive ? cipher.Passport?.Sex : null,
                BirthPlace = includeSensitive ? cipher.Passport?.BirthPlace : null,
                Nationality = cipher.Passport?.Nationality,
                IssuingCountry = cipher.Passport?.IssuingCountry,
                PassportNumber = includeSensitive ? cipher.Passport?.PassportNumber : null,
                PassportType = cipher.Passport?.PassportType,
                NationalIdentificationNumber = includeSensitive ? cipher.Passport?.NationalIdentificationNumber : null,
                IssuingAuthority = cipher.Passport?.IssuingAuthority,
                IssueDate = includeSensitive ? cipher.Passport?.IssueDate : null,
                ExpirationDate = includeSensitive ? cipher.Passport?.ExpirationDate : null,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(cipher), cipher.Type, "Unsupported cipher type."),
        };
    }

    private string? FolderNameFor(string? folderId) =>
        string.IsNullOrWhiteSpace(folderId)
            ? null
            : _vault.GetFolders().FirstOrDefault(folder => folder.Id == folderId)?.Name;

    // includeSensitive==false 时,隐藏(机密)字段的值固定为 string.Empty;标签与非隐藏值保留。
    private static IReadOnlyList<CustomField> MapFields(Cipher cipher, bool includeSensitive) =>
        cipher.Fields.Select(field =>
        {
            var type = field.Type switch
            {
                CipherFieldType.Hidden => CipherEditorFieldType.Hidden,
                CipherFieldType.Boolean => CipherEditorFieldType.Boolean,
                CipherFieldType.Linked => CipherEditorFieldType.Linked,
                CipherFieldType.Unsupported => CipherEditorFieldType.Unsupported,
                _ => CipherEditorFieldType.Text,
            };
            var resolvedValue = field.Type == CipherFieldType.Linked
                ? ResolveLinkedFieldValue(cipher, field.LinkedId)
                : field.Value;
            var value = (type is
                    CipherEditorFieldType.Hidden or
                    CipherEditorFieldType.Linked or
                    CipherEditorFieldType.Unsupported)
                && !includeSensitive
                ? string.Empty
                : resolvedValue ?? string.Empty;
            return new CustomField(field.Name, value, type);
        }).ToArray();

    private static string? ResolveLinkedFieldValue(Cipher cipher, int? linkedId) => linkedId switch
    {
        100 => cipher.Login?.Username,
        101 => cipher.Login?.Password,
        300 => cipher.Card?.CardholderName,
        301 => cipher.Card?.ExpMonth,
        302 => cipher.Card?.ExpYear,
        303 => cipher.Card?.Code,
        304 => cipher.Card?.Brand,
        305 => cipher.Card?.Number,
        400 => cipher.Identity?.Title,
        401 => cipher.Identity?.MiddleName,
        402 => cipher.Identity?.Address1,
        403 => cipher.Identity?.Address2,
        404 => cipher.Identity?.Address3,
        405 => cipher.Identity?.City,
        406 => cipher.Identity?.State,
        407 => cipher.Identity?.PostalCode,
        408 => cipher.Identity?.Country,
        409 => cipher.Identity?.Company,
        410 => cipher.Identity?.Email,
        411 => cipher.Identity?.Phone,
        412 => cipher.Identity?.Ssn,
        413 => cipher.Identity?.Username,
        414 => cipher.Identity?.PassportNumber,
        415 => cipher.Identity?.LicenseNumber,
        416 => cipher.Identity?.FirstName,
        417 => cipher.Identity?.LastName,
        418 => JoinNonEmpty(" ", cipher.Identity?.FirstName, cipher.Identity?.MiddleName, cipher.Identity?.LastName),
        _ => null,
    };

    private static IReadOnlyList<PasskeyDetail> MapPasskeys(IReadOnlyList<CipherFido2Credential>? credentials) =>
        (credentials ?? Array.Empty<CipherFido2Credential>())
            .Select(credential => new PasskeyDetail(
                credential.RpId,
                credential.UserName,
                credential.UserDisplayName,
                credential.Discoverable,
                credential.CreationDate))
            .ToArray();

    private static IReadOnlyList<AttachmentItem> MapAttachments(IReadOnlyList<CipherAttachment> attachments) =>
        attachments.Select(a => new AttachmentItem(a.Id, a.FileName, a.SizeName)).ToArray();

    private static VaultItemKind KindFor(CipherType type) => type switch
    {
        CipherType.Login => VaultItemKind.Login,
        CipherType.Card => VaultItemKind.Card,
        CipherType.Identity => VaultItemKind.Identity,
        CipherType.SecureNote => VaultItemKind.Note,
        CipherType.SshKey => VaultItemKind.Ssh,
        CipherType.BankAccount => VaultItemKind.BankAccount,
        CipherType.DriversLicense => VaultItemKind.DriversLicense,
        CipherType.Passport => VaultItemKind.Passport,
        _ => VaultItemKind.Login,
    };

    private static string GlyphFor(VaultItemKind kind) => kind switch
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

    private static bool CanEditContent(Cipher cipher, VaultCapabilities capabilities) =>
        cipher.CanEdit
        && cipher.CanViewPassword
        && CipherWritePolicy.CanWrite(cipher.Type, capabilities);

    private static string SubtitleFor(Cipher cipher) => cipher.Type switch
    {
        CipherType.Login => cipher.Login?.Username ?? string.Empty,
        CipherType.Card => cipher.Card?.Brand ?? "银行卡",
        CipherType.Identity => "身份",
        CipherType.SecureNote => "笔记",
        CipherType.SshKey => "SSH 密钥",
        CipherType.BankAccount => NonEmptyOrFallback(cipher.BankAccount?.BankName, "银行账户"),
        CipherType.DriversLicense => DocumentSubtitle(
            "驾驶证",
            JoinNonEmpty(" ", cipher.DriversLicense?.FirstName, cipher.DriversLicense?.MiddleName, cipher.DriversLicense?.LastName),
            JoinNonEmpty(", ", cipher.DriversLicense?.IssuingState, cipher.DriversLicense?.IssuingCountry)),
        CipherType.Passport => DocumentSubtitle(
            "护照",
            JoinNonEmpty(" ", cipher.Passport?.GivenName, cipher.Passport?.Surname),
            cipher.Passport?.IssuingCountry),
        _ => string.Empty,
    };

    private static string DocumentSubtitle(string fallback, string? name, string? issuingLocation) =>
        JoinNonEmpty(" · ", name, issuingLocation) ?? fallback;

    private static string NonEmptyOrFallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string? JoinExpiry(string? month, string? year) => JoinNonEmpty("/", month, year);

    private static string? JoinNonEmpty(string separator, params string?[] parts)
    {
        var values = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return values.Length == 0 ? null : string.Join(separator, values);
    }
}
