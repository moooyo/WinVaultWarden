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
    private readonly List<(CipherDetail Detail, string? FolderId)> _localDetails = new();

    public VaultUiService(IVaultService vault) => _vault = vault;

    public IReadOnlyList<CipherListItem> GetItems()
    {
        var domainItems = _vault.GetCiphers().Select(ToListItem);
        var localItems = _localDetails.Select(item => ToListItem(item.Detail, item.FolderId));
        return domainItems.Concat(localItems).ToList();
    }

    public CipherDetail GetDetail(string id)
    {
        var local = _localDetails.FirstOrDefault(item => item.Detail.Id == id);
        if (local.Detail is not null)
            return local.Detail;

        var cipher = _vault.GetCiphers().First(c => c.Id == id);
        return ToDetail(cipher);
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

    public void AddCipher(CipherDetail detail, string? folderId) => _localDetails.Add((detail, folderId));

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

    private static CipherListItem ToListItem(CipherDetail detail, string? folderId) => new()
    {
        Id = detail.Id,
        Name = detail.Name,
        Kind = detail.Kind,
        Subtitle = SubtitleFor(detail),
        Glyph = GlyphFor(detail.Kind),
        Favorite = detail.Favorite,
        FolderId = folderId,
        IsDeleted = detail.IsDeleted,
    };

    private CipherDetail ToDetail(Cipher cipher)
    {
        var commonFields = MapFields(cipher.Fields);
        var folderName = FolderNameFor(cipher.FolderId);

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
                Username = cipher.Login?.Username,
                Password = cipher.Login?.Password,
                TotpSecret = cipher.Login?.Totp,
                Uri = cipher.Login?.Uris.FirstOrDefault()?.Uri,
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

    private static string SubtitleFor(CipherDetail detail) => detail switch
    {
        LoginDetail login => login.Username ?? string.Empty,
        CardDetail card => card.Brand ?? "银行卡",
        IdentityDetail => "身份",
        NoteDetail => "笔记",
        SshDetail => "SSH 密钥",
        _ => string.Empty,
    };

    private static string? JoinExpiry(string? month, string? year) => JoinNonEmpty("/", month, year);

    private static string? JoinNonEmpty(string separator, params string?[] parts)
    {
        var values = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return values.Length == 0 ? null : string.Join(separator, values);
    }
}
