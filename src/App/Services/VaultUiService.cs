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
    private const string GlyphLogin = "\uE8D7";
    private const string GlyphCard = "\uE8C7";
    private const string GlyphIdentity = "\uE77B";
    private const string GlyphNote = "\uE70B";
    private const string GlyphSsh = "\uE192";
    private const string GlyphFolder = "\uE8B7";

    private readonly IVaultService _vault;
    private readonly IVaultWriteService _writeService;
    private readonly ISyncService _sync;
    private readonly VaultSession? _session;

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
    public bool DataStale => _vault.Snapshot.DataStale;

    public IReadOnlyList<CipherListItem> GetItems() => _vault.GetCiphers().Select(ToListItem).ToList();

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

    public CipherEditorDraft GetDraft(string id) =>
        CipherDraftMapper.ToDraft(_vault.GetCiphers().First(c => c.Id == id));

    public async Task<string> SaveCipherAsync(CipherEditorDraft draft, string? editingId, CancellationToken ct = default)
    {
        var original = string.IsNullOrEmpty(editingId)
            ? null
            : _vault.GetCiphers().FirstOrDefault(c => c.Id == editingId);
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
            // 新建:写前/写后 cipher id 差集定位新条目(re-sync 已重建快照)。
            var before = _vault.GetCiphers().Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            await _writeService.SaveCipherAsync(cipher, ct);
            var created = _vault.GetCiphers().FirstOrDefault(c => !before.Contains(c.Id));
            return created?.Id ?? string.Empty;
        }

        await _writeService.SaveCipherAsync(cipher, ct);
        return editingId!;
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

    public IReadOnlyList<FilterNode> GetFilters()
    {
        var items = GetItems();
        int CountType(VaultItemKind kind) => items.Count(i => i.Kind == kind && !i.IsDeleted);

        var filters = new List<FilterNode>
        {
            new() { Label = "所有项目", Glyph = GlyphAllItems, Kind = FilterKind.AllItems, Count = items.Count(i => !i.IsDeleted) },
            new() { Label = "收藏", Glyph = GlyphFavorite, Kind = FilterKind.Favorites, Count = items.Count(i => i.Favorite && !i.IsDeleted) },
            new() { Label = "回收站", Glyph = GlyphTrash, Kind = FilterKind.Trash, Count = items.Count(i => i.IsDeleted) },
            new() { Label = "登录", Glyph = GlyphLogin, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Login, Count = CountType(VaultItemKind.Login) },
            new() { Label = "银行卡", Glyph = GlyphCard, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Card, Count = CountType(VaultItemKind.Card) },
            new() { Label = "身份", Glyph = GlyphIdentity, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Identity, Count = CountType(VaultItemKind.Identity) },
            new() { Label = "笔记", Glyph = GlyphNote, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Note, Count = CountType(VaultItemKind.Note) },
            new() { Label = "SSH 密钥", Glyph = GlyphSsh, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Ssh, Count = CountType(VaultItemKind.Ssh) },
        };

        filters.AddRange(_vault.GetFolders().Select(folder => new FilterNode
        {
            Label = folder.Name,
            Glyph = GlyphFolder,
            Kind = FilterKind.Folder,
            FolderId = folder.Id,
            Count = items.Count(item => item.FolderId == folder.Id && !item.IsDeleted),
        }));

        return filters;
    }

    private CipherListItem ToListItem(Cipher cipher) => new()
    {
        Id = cipher.Id,
        Name = cipher.Name,
        Kind = KindFor(cipher.Type),
        Subtitle = SubtitleFor(cipher),
        Glyph = GlyphFor(KindFor(cipher.Type)),
        Favorite = cipher.Favorite,
        FolderId = cipher.FolderId,
        IsDeleted = cipher.IsDeleted,
        IconDomain = cipher.Type == CipherType.Login
            ? Core.IconDomain.Extract(cipher.Login?.Uris.FirstOrDefault()?.Uri)
            : null,
        SearchHaystack = Vault.VaultSearch.BuildHaystack(cipher),
        HasTotp = !string.IsNullOrEmpty(cipher.Login?.Totp),
        HasAttachment = cipher.Attachments.Count > 0,
        HasUri = cipher.Login?.Uris.Any(u => !string.IsNullOrEmpty(u.Uri)) == true,
        RevisionDate = cipher.RevisionDate,
        CreationDate = cipher.CreationDate,
    };

    // 详情投影。includeSensitive==false 时抹除全部秘密:密码/TOTP/密码历史/卡号/CVV/证件号/私钥/笔记内容/备注,
    // 且隐藏自定义字段值置空。SensitiveUnlocked 反映本投影是否已解锁。
    private CipherDetail ToDetail(Cipher cipher, bool includeSensitive)
    {
        var commonFields = MapFields(cipher.Fields, includeSensitive);
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
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt,
                SensitiveUnlocked = includeSensitive,
                Attachments = attachments,
                PublicKey = cipher.Ssh?.PublicKey,
                PrivateKey = includeSensitive ? cipher.Ssh?.PrivateKey : null,
                Fingerprint = cipher.Ssh?.Fingerprint,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(cipher), cipher.Type, "Unsupported cipher type."),
        };
    }

    private string? FolderNameFor(string? folderId) =>
        string.IsNullOrWhiteSpace(folderId)
            ? null
            : _vault.GetFolders().FirstOrDefault(folder => folder.Id == folderId)?.Name;

    // includeSensitive==false 时,隐藏(机密)字段的值固定为 string.Empty;标签与非隐藏值保留。
    private static IReadOnlyList<CustomField> MapFields(IReadOnlyList<CipherField> fields, bool includeSensitive) =>
        fields.Select(field =>
        {
            var type = field.Type switch
            {
                CipherFieldType.Hidden => CipherEditorFieldType.Hidden,
                CipherFieldType.Boolean => CipherEditorFieldType.Boolean,
                _ => CipherEditorFieldType.Text,
            };
            var value = type == CipherEditorFieldType.Hidden && !includeSensitive
                ? string.Empty
                : field.Value ?? string.Empty;
            return new CustomField(field.Name, value, type);
        }).ToArray();

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
        _ => VaultItemKind.Login,
    };

    private static string GlyphFor(VaultItemKind kind) => kind switch
    {
        VaultItemKind.Login => GlyphLogin,
        VaultItemKind.Card => GlyphCard,
        VaultItemKind.Identity => GlyphIdentity,
        VaultItemKind.Note => GlyphNote,
        VaultItemKind.Ssh => GlyphSsh,
        _ => GlyphLogin,
    };

    private static string SubtitleFor(Cipher cipher) => cipher.Type switch
    {
        CipherType.Login => cipher.Login?.Username ?? string.Empty,
        CipherType.Card => cipher.Card?.Brand ?? "银行卡",
        CipherType.Identity => "身份",
        CipherType.SecureNote => "笔记",
        CipherType.SshKey => "SSH 密钥",
        _ => string.Empty,
    };

    private static string? JoinExpiry(string? month, string? year) => JoinNonEmpty("/", month, year);

    private static string? JoinNonEmpty(string separator, params string?[] parts)
    {
        var values = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return values.Length == 0 ? null : string.Join(separator, values);
    }
}
