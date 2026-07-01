using System.Text.Json.Serialization;
using Core.Models;

namespace Vault.Porting;

// Bitwarden 未加密 JSON 导出格式的 DTO 镜像（{encrypted:false, folders, items}）。
// 纯数据传输对象，不含业务逻辑；域 <-> DTO 映射在 BitwardenJsonCodec 中完成。

public sealed record BwExport(bool Encrypted, IReadOnlyList<BwFolder> Folders, IReadOnlyList<BwItem> Items);

public sealed record BwFolder(string? Id, string? Name);

public sealed record BwItem(
    string? Id,
    string? OrganizationId,
    string? FolderId,
    int Type,
    string? Name,
    string? Notes,
    bool Favorite,
    int Reprompt,
    IReadOnlyList<string>? CollectionIds,
    BwLogin? Login,
    BwCard? Card,
    BwIdentity? Identity,
    BwSecureNote? SecureNote,
    BwSshKey? SshKey,
    IReadOnlyList<BwField>? Fields,
    IReadOnlyList<BwPasswordHistory>? PasswordHistory);

public sealed record BwLogin(string? Username, string? Password, string? Totp, IReadOnlyList<BwUri>? Uris);

public sealed record BwUri(string? Uri, int? Match);

public sealed record BwCard(
    string? CardholderName,
    string? Number,
    string? ExpMonth,
    string? ExpYear,
    string? Code,
    string? Brand);

public sealed record BwIdentity(
    string? Title,
    string? FirstName,
    string? MiddleName,
    string? LastName,
    string? Username,
    string? Company,
    string? Ssn,
    string? PassportNumber,
    string? LicenseNumber,
    string? Email,
    string? Phone,
    string? Address1,
    string? Address2,
    string? Address3,
    string? City,
    string? State,
    string? PostalCode,
    string? Country);

public sealed record BwSecureNote(int Type);

public sealed record BwSshKey(string? PrivateKey, string? PublicKey, string? KeyFingerprint);

public sealed record BwField(string? Name, string? Value, int Type);

public sealed record BwPasswordHistory(string? Password, string? LastUsedDate);

// domain <-> DTO 转换的共享结果：解析出的 ciphers/folders + folder 关系
// (cipher 在列表的索引, folder 在列表的索引)。CSV codec（Task 2）复用同一类型。
public sealed record PortingData(
    IReadOnlyList<Cipher> Ciphers,
    IReadOnlyList<Folder> Folders,
    IReadOnlyList<(int CipherIndex, int FolderIndex)> Relations);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BwExport))]
internal partial class BwJsonContext : JsonSerializerContext
{
}
