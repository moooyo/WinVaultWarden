using System.Text.Json.Serialization;

namespace Api.Dtos;

// 写入密码库条目的请求体,镜像 Vaultwarden 的 CipherData(camelCase)。
// 字段加密在 Vault.CipherEncryptor 中完成,这里只承载已加密的 EncString 文本。
public class CipherRequest
{
    public CipherRequest()
    {
    }

    protected CipherRequest(CipherRequest source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Type = source.Type;
        EncryptedFor = source.EncryptedFor;
        EncryptedByKeyId = source.EncryptedByKeyId;
        Name = source.Name;
        Notes = source.Notes;
        FolderId = source.FolderId;
        OrganizationId = source.OrganizationId;
        Key = source.Key;
        Favorite = source.Favorite;
        Reprompt = source.Reprompt;
        Fields = source.Fields;
        Login = source.Login;
        Card = source.Card;
        Identity = source.Identity;
        SecureNote = source.SecureNote;
        SshKey = source.SshKey;
        BankAccount = source.BankAccount;
        DriversLicense = source.DriversLicense;
        Passport = source.Passport;
        ArchivedDate = source.ArchivedDate;
        LastKnownRevisionDate = source.LastKnownRevisionDate;
        PasswordHistory = source.PasswordHistory;
        Data = source.Data;
    }

    public int Type { get; init; }
    public string? EncryptedFor { get; init; }
    public string? EncryptedByKeyId { get; init; }
    public string? Name { get; init; } = string.Empty;
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
    public BankAccountRequest? BankAccount { get; init; }
    public DriversLicenseRequest? DriversLicense { get; init; }
    public PassportRequest? Passport { get; init; }
    public DateTimeOffset? ArchivedDate { get; init; }
    public string? LastKnownRevisionDate { get; init; }
    public PasswordHistoryRequest[]? PasswordHistory { get; init; }
    public string? Data { get; init; }

    public CipherRequest WithEncryptionContext(
        string encryptedFor,
        string? encryptedByKeyId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedFor);
        return new CipherRequest(this)
        {
            EncryptedFor = encryptedFor,
            EncryptedByKeyId = encryptedByKeyId ?? EncryptedByKeyId,
        };
    }
}

public sealed class ShareCipherRequest
{
    public CipherRequest Cipher { get; init; } = new();
    public string[] CollectionIds { get; init; } = Array.Empty<string>();
}

public sealed class CipherWithIdRequest : CipherRequest
{
    public CipherWithIdRequest()
    {
    }

    public CipherWithIdRequest(string id, CipherRequest cipher)
        : base(cipher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    public string Id { get; init; } = string.Empty;
}

public sealed class BulkShareCiphersRequest
{
    public CipherWithIdRequest[] Ciphers { get; init; } = Array.Empty<CipherWithIdRequest>();
    public string[] CollectionIds { get; init; } = Array.Empty<string>();
}

public sealed class CipherCollectionsRequest
{
    public string[] CollectionIds { get; init; } = Array.Empty<string>();
}

public sealed class BulkCipherCollectionsRequest
{
    public string OrganizationId { get; init; } = string.Empty;
    public string[] CipherIds { get; init; } = Array.Empty<string>();
    public string[] CollectionIds { get; init; } = Array.Empty<string>();
    public bool RemoveCollections { get; init; }
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
    public DateTimeOffset? PasswordRevisionDate { get; init; }
    public bool? AutofillOnPageLoad { get; init; }
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

public sealed class BankAccountRequest
{
    public string? BankName { get; init; }
    public string? NameOnAccount { get; init; }
    public string? AccountType { get; init; }
    public string? AccountNumber { get; init; }
    public string? RoutingNumber { get; init; }
    public string? BranchNumber { get; init; }
    public string? Pin { get; init; }
    public string? SwiftCode { get; init; }
    public string? Iban { get; init; }
    public string? BankContactPhone { get; init; }
}

public sealed class DriversLicenseRequest
{
    public string? FirstName { get; init; }
    public string? MiddleName { get; init; }
    public string? LastName { get; init; }
    public string? DateOfBirth { get; init; }
    public string? LicenseNumber { get; init; }
    public string? IssuingCountry { get; init; }
    public string? IssuingState { get; init; }
    public string? IssueDate { get; init; }
    public string? ExpirationDate { get; init; }
    public string? IssuingAuthority { get; init; }
    public string? LicenseClass { get; init; }
}

public sealed class PassportRequest
{
    public string? Surname { get; init; }
    public string? GivenName { get; init; }
    public string? DateOfBirth { get; init; }
    public string? Sex { get; init; }
    public string? BirthPlace { get; init; }
    public string? Nationality { get; init; }
    public string? IssuingCountry { get; init; }
    public string? PassportNumber { get; init; }
    public string? PassportType { get; init; }
    public string? NationalIdentificationNumber { get; init; }
    public string? IssuingAuthority { get; init; }
    public string? IssueDate { get; init; }
    public string? ExpirationDate { get; init; }
}

public sealed class FieldRequest
{
    public int Type { get; init; }
    public string? Name { get; init; }
    public string? Value { get; init; }
    public int? LinkedId { get; init; }
}

// Vaultwarden 4xx 错误体:{"message":"...","validationErrors":{"":["msg"]},"object":"error"}
// validationErrors 为 HashMap<string, Vec<string>>(字段名 -> 错误消息列表),见 vaultwarden/src/error.rs。
public sealed record WriteErrorResponse(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("validationErrors")] Dictionary<string, string[]>? ValidationErrors = null);

// 批量操作请求体（镜像 Vaultwarden CipherIdsData / MoveCipherData，camelCase）。
public sealed class CipherIdsRequest
{
    public string[] Ids { get; init; } = Array.Empty<string>();
}

public sealed class PartialCipherRequest
{
    public string? FolderId { get; init; }
    public bool Favorite { get; init; }
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
