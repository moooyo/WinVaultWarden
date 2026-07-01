using System.Text.Json.Serialization;

namespace Api.Dtos;

// 写入密码库条目的请求体,镜像 Vaultwarden 的 CipherData(camelCase)。
// 字段加密在 Vault.CipherEncryptor 中完成,这里只承载已加密的 EncString 文本。
public sealed class CipherRequest
{
    public int Type { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Notes { get; init; }
    public string? FolderId { get; init; }
    public string? OrganizationId { get; init; }
    public string? Key { get; init; }
    public bool Favorite { get; init; }
    public int Reprompt { get; init; }
    public FieldRequest[]? Fields { get; init; }
    public LoginRequest? Login { get; init; }
    public CardRequest? Card { get; init; }
    public IdentityRequest? Identity { get; init; }
    public SecureNoteRequest? SecureNote { get; init; }
    public SshKeyRequest? SshKey { get; init; }
    public string? LastKnownRevisionDate { get; init; }
    public PasswordHistoryRequest[]? PasswordHistory { get; init; }
}

public sealed class PasswordHistoryRequest
{
    public string? Password { get; init; }
    public string? LastUsedDate { get; init; }
}

public sealed class LoginRequest
{
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Totp { get; init; }
    public LoginUriRequest[]? Uris { get; init; }
    public Fido2CredentialRequest[]? Fido2Credentials { get; init; }
}

public sealed class LoginUriRequest
{
    public string? Uri { get; init; }
    public int? Match { get; init; }
}

public sealed class Fido2CredentialRequest
{
    public string? CredentialId { get; init; }
    public string? KeyType { get; init; }
    public string? KeyAlgorithm { get; init; }
    public string? KeyCurve { get; init; }
    public string? KeyValue { get; init; }
    public string? RpId { get; init; }
    public string? UserHandle { get; init; }
    public string? UserName { get; init; }
    public string? Counter { get; init; }
    public string? RpName { get; init; }
    public string? UserDisplayName { get; init; }
    public string? Discoverable { get; init; }
    public DateTimeOffset? CreationDate { get; init; }
}

public sealed class CardRequest
{
    public string? CardholderName { get; init; }
    public string? Brand { get; init; }
    public string? Number { get; init; }
    public string? ExpMonth { get; init; }
    public string? ExpYear { get; init; }
    public string? Code { get; init; }
}

public sealed class IdentityRequest
{
    public string? Title { get; init; }
    public string? FirstName { get; init; }
    public string? MiddleName { get; init; }
    public string? LastName { get; init; }
    public string? Username { get; init; }
    public string? Company { get; init; }
    public string? Ssn { get; init; }
    public string? PassportNumber { get; init; }
    public string? LicenseNumber { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Address1 { get; init; }
    public string? Address2 { get; init; }
    public string? Address3 { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? PostalCode { get; init; }
    public string? Country { get; init; }
}

public sealed class SecureNoteRequest
{
    public int Type { get; init; }
}

public sealed class SshKeyRequest
{
    public string? PrivateKey { get; init; }
    public string? PublicKey { get; init; }
    public string? KeyFingerprint { get; init; }
}

public sealed class FieldRequest
{
    public int Type { get; init; }
    public string? Name { get; init; }
    public string? Value { get; init; }
}

// Vaultwarden 4xx 错误体:{"message":"...","validationErrors":{...},"object":"error"}
public sealed record WriteErrorResponse(
    [property: JsonPropertyName("message")] string? Message);

// 批量操作请求体（镜像 Vaultwarden CipherIdsData / MoveCipherData，camelCase）。
public sealed class CipherIdsRequest
{
    public string[] Ids { get; init; } = Array.Empty<string>();
}

public sealed class MoveCiphersRequest
{
    public string[] Ids { get; init; } = Array.Empty<string>();
    public string? FolderId { get; init; }   // null = 移到根（无文件夹）
}

// 批量导入请求体（镜像 Vaultwarden ImportData / post_ciphers_import,camelCase）。
// 文件夹归属由 folderRelationships 表达(cipher 下标 -> folder 下标),
// 而非各 CipherRequest.FolderId ——服务端按此映射建立关联,详见 src/api/core/ciphers.rs。
public sealed class ImportRequest
{
    public CipherRequest[] Ciphers { get; init; } = Array.Empty<CipherRequest>();
    public FolderRequest[] Folders { get; init; } = Array.Empty<FolderRequest>();
    public ImportRelationship[] FolderRelationships { get; init; } = Array.Empty<ImportRelationship>();
}

public sealed class ImportRelationship
{
    public int Key { get; init; }
    public int Value { get; init; }
}
