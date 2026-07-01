using System.Globalization;
using System.Text.Json;
using Core.Enums;
using Core.Models;

namespace Vault.Porting;

// 域模型 <-> Bitwarden 未加密 JSON 导出格式的无损编解码器。
// 纯函数：不涉及网络、加密；调用方负责在导出前解密、导入后加密。
public static class BitwardenJsonCodec
{
    public static string Serialize(IReadOnlyList<Cipher> ciphers, IReadOnlyList<Folder> folders)
    {
        // folder 用其在列表中的 index 作为稳定 id，item.folderId 引用该 id。
        var folderIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < folders.Count; i++)
        {
            if (!string.IsNullOrEmpty(folders[i].Id))
                folderIndexById[folders[i].Id] = i;
        }

        var bwFolders = folders.Select((f, i) => new BwFolder(i.ToString(CultureInfo.InvariantCulture), f.Name)).ToArray();

        var bwItems = ciphers.Select(c =>
        {
            string? folderId = null;
            if (!string.IsNullOrEmpty(c.FolderId) && folderIndexById.TryGetValue(c.FolderId, out var idx))
                folderId = idx.ToString(CultureInfo.InvariantCulture);

            return new BwItem(
                Id: c.Id,
                OrganizationId: null,
                FolderId: folderId,
                Type: (int)c.Type,
                Name: c.Name,
                Notes: c.Notes,
                Favorite: c.Favorite,
                Reprompt: c.Reprompt ? 1 : 0,
                CollectionIds: null,
                Login: c.Login is null ? null : ToBw(c.Login),
                Card: c.Card is null ? null : ToBw(c.Card),
                Identity: c.Identity is null ? null : ToBw(c.Identity),
                SecureNote: c.SecureNote is null ? null : new BwSecureNote(c.SecureNote.Type),
                SshKey: c.Ssh is null ? null : ToBw(c.Ssh),
                Fields: c.Fields.Count == 0 ? null : c.Fields.Select(ToBw).ToArray(),
                PasswordHistory: c.PasswordHistory.Count == 0 ? null : c.PasswordHistory.Select(ToBw).ToArray());
        }).ToArray();

        var export = new BwExport(Encrypted: false, Folders: bwFolders, Items: bwItems);
        return JsonSerializer.Serialize(export, BwJsonContext.Default.BwExport);
    }

    public static PortingData Parse(string json)
    {
        var export = JsonSerializer.Deserialize(json, BwJsonContext.Default.BwExport)
            ?? throw new JsonException("Bitwarden export JSON 为空或无法解析。");

        var folders = export.Folders.Select(f => new Folder
        {
            Id = f.Id ?? string.Empty,
            Name = f.Name ?? string.Empty,
        }).ToArray();

        var folderIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < export.Folders.Count; i++)
        {
            var fid = export.Folders[i].Id;
            if (!string.IsNullOrEmpty(fid))
                folderIndexById[fid] = i;
        }

        var ciphers = new List<Cipher>(export.Items.Count);
        var relations = new List<(int CipherIndex, int FolderIndex)>();

        for (var i = 0; i < export.Items.Count; i++)
        {
            var item = export.Items[i];
            ciphers.Add(FromBw(item));

            if (!string.IsNullOrEmpty(item.FolderId) && folderIndexById.TryGetValue(item.FolderId, out var folderIdx))
                relations.Add((i, folderIdx));
        }

        return new PortingData(ciphers, folders, relations);
    }

    private static Cipher FromBw(BwItem item) => new()
    {
        Id = item.Id ?? string.Empty,
        // 未知/非法 type（如手工篡改的导出文件）回退为 Login —— Bitwarden 以登录为中心的默认类型，
        // 保持导入侧的健壮性与可加性而非抛出异常中断整批导入。
        Type = Enum.IsDefined(typeof(CipherType), item.Type) ? (CipherType)item.Type : CipherType.Login,
        FolderId = item.FolderId,
        Favorite = item.Favorite,
        Reprompt = item.Reprompt != 0,
        Name = item.Name ?? string.Empty,
        Notes = item.Notes,
        Login = item.Login is null ? null : FromBw(item.Login),
        Card = item.Card is null ? null : FromBw(item.Card),
        Identity = item.Identity is null ? null : FromBw(item.Identity),
        SecureNote = item.SecureNote is null ? null : new CipherSecureNote(item.SecureNote.Type),
        Ssh = item.SshKey is null ? null : FromBw(item.SshKey),
        Fields = item.Fields is null ? Array.Empty<CipherField>() : item.Fields.Select(FromBw).ToArray(),
        PasswordHistory = item.PasswordHistory is null
            ? Array.Empty<PasswordHistoryEntry>()
            : item.PasswordHistory.Select(FromBw).ToArray(),
    };

    private static BwLogin ToBw(CipherLogin l) => new(
        l.Username, l.Password, l.Totp,
        l.Uris.Count == 0 ? null : l.Uris.Select(u => new BwUri(u.Uri, u.Match)).ToArray());

    private static CipherLogin FromBw(BwLogin l) => new(
        l.Username, l.Password, l.Totp,
        l.Uris is null ? Array.Empty<CipherLoginUri>() : l.Uris.Select(u => new CipherLoginUri(u.Uri, u.Match)).ToArray());

    private static BwCard ToBw(CipherCard c) => new(c.CardholderName, c.Number, c.ExpMonth, c.ExpYear, c.Code, c.Brand);

    private static CipherCard FromBw(BwCard c) => new(c.CardholderName, c.Number, c.ExpMonth, c.ExpYear, c.Code, c.Brand);

    private static BwIdentity ToBw(CipherIdentity i) => new(
        i.Title, i.FirstName, i.MiddleName, i.LastName, i.Username, i.Company, i.Ssn, i.PassportNumber,
        i.LicenseNumber, i.Email, i.Phone, i.Address1, i.Address2, i.Address3, i.City, i.State, i.PostalCode, i.Country);

    private static CipherIdentity FromBw(BwIdentity i) => new(
        i.Title, i.FirstName, i.MiddleName, i.LastName, i.Username, i.Company, i.Ssn, i.PassportNumber,
        i.LicenseNumber, i.Email, i.Phone, i.Address1, i.Address2, i.Address3, i.City, i.State, i.PostalCode, i.Country);

    private static BwSshKey ToBw(CipherSsh s) => new(s.PrivateKey, s.PublicKey, s.Fingerprint);

    private static CipherSsh FromBw(BwSshKey s) => new(s.PrivateKey, s.PublicKey, s.KeyFingerprint);

    private static BwField ToBw(CipherField f) => new(f.Name, f.Value, (int)f.Type);

    private static CipherField FromBw(BwField f) => new(f.Name ?? string.Empty, f.Value, (CipherFieldType)f.Type);

    private static BwPasswordHistory ToBw(PasswordHistoryEntry p) => new(
        p.Password, p.LastUsedDate?.ToString("o", CultureInfo.InvariantCulture));

    private static PasswordHistoryEntry FromBw(BwPasswordHistory p)
    {
        DateTimeOffset? lastUsed = null;
        if (!string.IsNullOrEmpty(p.LastUsedDate) &&
            DateTimeOffset.TryParse(p.LastUsedDate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            lastUsed = parsed;
        }

        return new PasswordHistoryEntry(p.Password ?? string.Empty, lastUsed);
    }
}
