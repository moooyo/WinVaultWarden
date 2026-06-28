using App.ViewModels.Models;
using Core.Enums;
using Core.Models;
using Core.Services;

namespace App.Services;

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

    public VaultUiService(IVaultService vault, IVaultWriteService writeService, ISyncService sync)
    {
        _vault = vault;
        _writeService = writeService;
        _sync = sync;
    }

    public IReadOnlyList<CipherListItem> GetItems() => _vault.GetCiphers().Select(ToListItem).ToList();

    public CipherDetail GetDetail(string id) => ToDetail(_vault.GetCiphers().First(c => c.Id == id));

    public CipherEditorDraft GetDraft(string id) =>
        CipherDraftMapper.ToDraft(_vault.GetCiphers().First(c => c.Id == id));

    public async Task<string> SaveCipherAsync(CipherEditorDraft draft, string? editingId, CancellationToken ct = default)
    {
        var original = string.IsNullOrEmpty(editingId)
            ? null
            : _vault.GetCiphers().FirstOrDefault(c => c.Id == editingId);
        var cipher = CipherDraftMapper.ToCipher(draft, original);

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

    public Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default) =>
        _writeService.DeleteCipherAsync(id, permanent, ct);

    public Task RestoreCipherAsync(string id, CancellationToken ct = default) =>
        _writeService.RestoreCipherAsync(id, ct);

    public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) =>
        _writeService.SaveFolderAsync(folderId, name, ct);

    public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) =>
        _writeService.DeleteFolderAsync(folderId, ct);

    public Task SyncAsync(CancellationToken ct = default) => _sync.SyncAsync(ct);

    public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) =>
        throw new NotImplementedException("移动到文件夹功能尚未接入。");

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
    };

    private CipherDetail ToDetail(Cipher cipher)
    {
        var commonFields = MapFields(cipher.Fields);
        var folderName = FolderNameFor(cipher.FolderId);
        var attachments = MapAttachments(cipher.Attachments);

        return cipher.Type switch
        {
            CipherType.Login => new LoginDetail
            {
                Id = cipher.Id,
                Name = cipher.Name,
                FolderName = folderName,
                Notes = cipher.Notes,
                CustomFields = commonFields,
                Created = cipher.CreationDate,
                Edited = cipher.RevisionDate,
                IsDeleted = cipher.IsDeleted,
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt,
                Attachments = attachments,
                Username = cipher.Login?.Username,
                Password = cipher.Login?.Password,
                TotpSecret = cipher.Login?.Totp,
                Uri = cipher.Login?.Uris.FirstOrDefault()?.Uri,
                Passkeys = MapPasskeys(cipher.Login?.Fido2Credentials),
            },
            CipherType.Card => new CardDetail
            {
                Id = cipher.Id,
                Name = cipher.Name,
                FolderName = folderName,
                Notes = cipher.Notes,
                CustomFields = commonFields,
                Created = cipher.CreationDate,
                Edited = cipher.RevisionDate,
                IsDeleted = cipher.IsDeleted,
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt,
                Attachments = attachments,
                Cardholder = cipher.Card?.CardholderName,
                Number = cipher.Card?.Number,
                Expiry = JoinExpiry(cipher.Card?.ExpMonth, cipher.Card?.ExpYear),
                Brand = cipher.Card?.Brand,
                Cvv = cipher.Card?.Code,
            },
            CipherType.Identity => new IdentityDetail
            {
                Id = cipher.Id,
                Name = cipher.Name,
                FolderName = folderName,
                Notes = cipher.Notes,
                CustomFields = commonFields,
                Created = cipher.CreationDate,
                Edited = cipher.RevisionDate,
                IsDeleted = cipher.IsDeleted,
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt,
                Attachments = attachments,
                FullName = JoinNonEmpty(" ", cipher.Identity?.FirstName, cipher.Identity?.MiddleName, cipher.Identity?.LastName),
                Username = cipher.Identity?.Username,
                Company = cipher.Identity?.Company,
                Email = cipher.Identity?.Email,
                Phone = cipher.Identity?.Phone,
                IdNumber = cipher.Identity?.Ssn ?? cipher.Identity?.PassportNumber ?? cipher.Identity?.LicenseNumber,
                Address = JoinNonEmpty(", ", cipher.Identity?.Address1, cipher.Identity?.Address2, cipher.Identity?.Address3,
                    cipher.Identity?.City, cipher.Identity?.State, cipher.Identity?.PostalCode, cipher.Identity?.Country),
            },
            CipherType.SecureNote => new NoteDetail
            {
                Id = cipher.Id,
                Name = cipher.Name,
                FolderName = folderName,
                Notes = cipher.Notes,
                CustomFields = commonFields,
                Created = cipher.CreationDate,
                Edited = cipher.RevisionDate,
                IsDeleted = cipher.IsDeleted,
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt,
                Attachments = attachments,
                Content = cipher.Notes,
            },
            CipherType.SshKey => new SshDetail
            {
                Id = cipher.Id,
                Name = cipher.Name,
                FolderName = folderName,
                Notes = cipher.Notes,
                CustomFields = commonFields,
                Created = cipher.CreationDate,
                Edited = cipher.RevisionDate,
                IsDeleted = cipher.IsDeleted,
                Favorite = cipher.Favorite,
                Reprompt = cipher.Reprompt,
                Attachments = attachments,
                PublicKey = cipher.Ssh?.PublicKey,
                PrivateKey = cipher.Ssh?.PrivateKey,
                Fingerprint = cipher.Ssh?.Fingerprint,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(cipher), cipher.Type, "Unsupported cipher type."),
        };
    }

    private string? FolderNameFor(string? folderId) =>
        string.IsNullOrWhiteSpace(folderId)
            ? null
            : _vault.GetFolders().FirstOrDefault(folder => folder.Id == folderId)?.Name;

    private static IReadOnlyList<CustomField> MapFields(IReadOnlyList<CipherField> fields) =>
        fields.Select(field => new CustomField(field.Name, field.Value ?? string.Empty, field.Type switch
        {
            CipherFieldType.Hidden => CipherEditorFieldType.Hidden,
            CipherFieldType.Boolean => CipherEditorFieldType.Boolean,
            _ => CipherEditorFieldType.Text,
        })).ToArray();

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
