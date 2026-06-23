using App.ViewModels.Models;

namespace App.Services;

// App 层 UI 数据服务(mock)。与 Core.IVaultService 分开:这是给界面用的展示数据。
// 纯 C#(只依赖 App 视图模型,不依赖 WinUI),便于被 App.Tests 链接测试。
public interface IVaultUiService
{
    IReadOnlyList<CipherListItem> GetItems();
    CipherDetail GetDetail(string id);
    IReadOnlyList<FilterNode> GetFilters();
}

public sealed class MockVaultUiService : IVaultUiService
{
    // Segoe Fluent Icons glyph 码(集中定义,避免散落的不可见字符)
    private const string GlyphAllItems = "";   // AllApps
    private const string GlyphFavorite = "";   // FavoriteStar
    private const string GlyphTrash = "";      // Delete
    private const string GlyphLogin = "";      // Permissions/Key
    private const string GlyphCard = "";       // PaymentCard
    private const string GlyphIdentity = "";   // Contact
    private const string GlyphNote = "";       // QuickNote
    private const string GlyphSsh = "";        // Key
    private const string GlyphFolder = "";     // Folder

    private readonly List<CipherDetail> _details = BuildDetails();

    public IReadOnlyList<CipherListItem> GetItems() =>
        _details.Select(d => new CipherListItem
        {
            Id = d.Id,
            Name = d.Name,
            Kind = d.Kind,
            Subtitle = SubtitleFor(d),
            Glyph = GlyphFor(d.Kind),
            Favorite = FavoriteFor(d.Id),
            FolderId = FolderFor(d.Id),
            IsDeleted = d.IsDeleted,
        }).ToList();

    public CipherDetail GetDetail(string id) => _details.First(d => d.Id == id);

    // 收藏:百度网盘(id=1)。
    private static bool FavoriteFor(string id) => id == "1";

    // 文件夹归属:百度网盘(id=1)属于 f1,其余无文件夹。
    private static string? FolderFor(string id) => id == "1" ? "f1" : null;

    public IReadOnlyList<FilterNode> GetFilters()
    {
        var items = GetItems();
        int CountType(VaultItemKind k) => items.Count(i => i.Kind == k && !i.IsDeleted);
        return new List<FilterNode>
        {
            new() { Label = "所有项目", Glyph = GlyphAllItems, Kind = FilterKind.AllItems, Count = items.Count(i => !i.IsDeleted) },
            new() { Label = "收藏", Glyph = GlyphFavorite, Kind = FilterKind.Favorites, Count = items.Count(i => i.Favorite && !i.IsDeleted) },
            new() { Label = "回收站", Glyph = GlyphTrash, Kind = FilterKind.Trash, Count = items.Count(i => i.IsDeleted) },
            new() { Label = "登录", Glyph = GlyphLogin, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Login, Count = CountType(VaultItemKind.Login) },
            new() { Label = "银行卡", Glyph = GlyphCard, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Card, Count = CountType(VaultItemKind.Card) },
            new() { Label = "身份", Glyph = GlyphIdentity, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Identity, Count = CountType(VaultItemKind.Identity) },
            new() { Label = "笔记", Glyph = GlyphNote, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Note, Count = CountType(VaultItemKind.Note) },
            new() { Label = "SSH 密钥", Glyph = GlyphSsh, Kind = FilterKind.Type, TypeFilter = VaultItemKind.Ssh, Count = CountType(VaultItemKind.Ssh) },
            new() { Label = "文件夹1", Glyph = GlyphFolder, Kind = FilterKind.Folder, FolderId = "f1", Count = items.Count(i => i.FolderId == "f1" && !i.IsDeleted) },
        };
    }

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

    private static List<CipherDetail> BuildDetails() => new()
    {
        new LoginDetail
        {
            Id = "1", Name = "百度网盘", FolderName = "文件夹1",
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
            Id = "6", Name = "已删除的旧账号", IsDeleted = true,
            Username = "old@example.com", Password = "obsolete",
            Created = new DateTimeOffset(2026, 1, 5, 6, 0, 0, TimeSpan.Zero),
            Edited = new DateTimeOffset(2026, 1, 10, 6, 0, 0, TimeSpan.Zero),
        },
    };
}
