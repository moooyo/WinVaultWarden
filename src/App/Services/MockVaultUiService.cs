using App.ViewModels.Models;

namespace App.Services;

// App 层 UI 数据服务(mock)。与 Core.IVaultService 分开:这是给界面用的展示数据。
// 纯 C#(只依赖 App 视图模型,不依赖 WinUI),便于被 App.Tests 链接测试。
public interface IVaultUiService
{
    IReadOnlyList<CipherListItem> GetItems();
    CipherDetail GetDetail(string id);
    IReadOnlyList<FilterNode> GetFilters();
    CipherEditorDraft GetDraft(string id);

    // 写入侧:每个方法 = 加密 → 写 API → 整库 re-sync(真实实现委托 IVaultWriteService)。
    Task<string> SaveCipherAsync(CipherEditorDraft draft, string? editingId, CancellationToken ct = default);
    Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default);
    Task RestoreCipherAsync(string id, CancellationToken ct = default);
    Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default);
    Task DeleteFolderAsync(string folderId, CancellationToken ct = default);
    Task SyncAsync(CancellationToken ct = default);
    Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default);
}

public sealed class MockVaultUiService : IVaultUiService
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

    private readonly List<CipherDetail> _details = BuildDetails();
    private readonly Dictionary<string, string?> _folderIds = new() { ["1"] = "f1" };
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal) { "6" };
    private readonly List<(string Id, string Name)> _folders = new() { ("f1", "文件夹1") };
    private int _nextId = 100;

    public IReadOnlyList<CipherListItem> GetItems() =>
        _details.Select(d => new CipherListItem
        {
            Id = d.Id,
            Name = d.Name,
            Kind = d.Kind,
            Subtitle = SubtitleFor(d),
            Glyph = GlyphFor(d.Kind),
            Favorite = d.Favorite,
            FolderId = _folderIds.TryGetValue(d.Id, out var folderId) ? folderId : null,
            IsDeleted = _deleted.Contains(d.Id),
        }).ToList();

    public CipherDetail GetDetail(string id) => _details.First(d => d.Id == id);

    public CipherEditorDraft GetDraft(string id) => DraftFor(_details.First(d => d.Id == id), FolderIdFor(id));

    public IReadOnlyList<FilterNode> GetFilters()
    {
        var items = GetItems();
        int CountType(VaultItemKind k) => items.Count(i => i.Kind == k && !i.IsDeleted);
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
        filters.AddRange(_folders.Select(folder => new FilterNode
        {
            Label = folder.Name,
            Glyph = GlyphFolder,
            Kind = FilterKind.Folder,
            FolderId = folder.Id,
            Count = items.Count(i => i.FolderId == folder.Id && !i.IsDeleted),
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
        }
        return Task.FromResult(editingId);
    }

    public Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default)
    {
        if (permanent)
        {
            _details.RemoveAll(d => d.Id == id);
            _deleted.Remove(id);
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

    private string? FolderIdFor(string id) => _folderIds.TryGetValue(id, out var f) ? f : null;

    private static string SubtitleFor(CipherDetail d) => d switch
    {
        LoginDetail l => l.Username ?? "",
        CardDetail c => c.Brand ?? "银行卡",
        IdentityDetail => "身份",
        NoteDetail => "笔记",
        SshDetail => "SSH 密钥",
        _ => "",
    };

    private static string GlyphFor(VaultItemKind k) => k switch
    {
        VaultItemKind.Login => GlyphLogin,
        VaultItemKind.Card => GlyphCard,
        VaultItemKind.Identity => GlyphIdentity,
        VaultItemKind.Note => GlyphNote,
        VaultItemKind.Ssh => GlyphSsh,
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
