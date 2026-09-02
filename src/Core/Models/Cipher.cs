using Core.Enums;

namespace Core.Models;

// 解密后的密码库条目领域模型。明文字段只允许存在于内存中。
public sealed class Cipher
{
    public string Id { get; init; } = string.Empty;
    public CipherType Type { get; init; }
    public string? OrganizationId { get; init; }
    public IReadOnlyList<string> CollectionIds { get; init; } = Array.Empty<string>();
    public string? FolderId { get; init; }
    // 服务端下发的、用 UserKey 包裹的条目密钥(EncString 密文,非明文)。
    // 编辑时必须原样复用它回发——轮换条目密钥会使既有附件的 akey 失配、永久损坏附件。
    // 空表示该条目字段直接以 UserKey 加密(旧格式,无每条目密钥)。
    public string? Key { get; init; }
    // Sensitive fields originated from the versioned Cipher.data container.
    public bool UsesBlobEncryption { get; init; }
    public bool Favorite { get; init; }
    public bool Reprompt { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Notes { get; init; }
    public DateTimeOffset CreationDate { get; init; }
    public DateTimeOffset RevisionDate { get; init; }
    public DateTimeOffset? DeletedDate { get; init; }
    public DateTimeOffset? ArchivedDate { get; init; }
    public bool IsDeleted => DeletedDate is not null;
    public bool IsArchived => ArchivedDate is not null;
    public bool CanEdit { get; init; } = true;
    public bool CanViewPassword { get; init; } = true;
    public bool CanDelete { get; init; } = true;
    public bool CanRestore { get; init; } = true;
    public CipherLogin? Login { get; init; }
    public CipherCard? Card { get; init; }
    public CipherIdentity? Identity { get; init; }
    public CipherSecureNote? SecureNote { get; init; }
    public CipherSsh? Ssh { get; init; }
    public CipherBankAccount? BankAccount { get; init; }
    public CipherDriversLicense? DriversLicense { get; init; }
    public CipherPassport? Passport { get; init; }
    public IReadOnlyList<CipherField> Fields { get; init; } = Array.Empty<CipherField>();
    public IReadOnlyList<CipherAttachment> Attachments { get; init; } = Array.Empty<CipherAttachment>();
    public IReadOnlyList<PasswordHistoryEntry> PasswordHistory { get; init; } = Array.Empty<PasswordHistoryEntry>();

    // 返回一个仅 FolderId 不同的副本(Cipher 为不可变 class,无 record 的 with)。
    // 用于删除文件夹时把引用它的条目本地迁到"无文件夹"。新增属性时须同步此拷贝。
    public Cipher WithFolderId(string? folderId) => new()
    {
        Id = Id,
        Type = Type,
        OrganizationId = OrganizationId,
        CollectionIds = CollectionIds,
        FolderId = folderId,
        Key = Key,
        UsesBlobEncryption = UsesBlobEncryption,
        Favorite = Favorite,
        Reprompt = Reprompt,
        Name = Name,
        Notes = Notes,
        CreationDate = CreationDate,
        RevisionDate = RevisionDate,
        DeletedDate = DeletedDate,
        ArchivedDate = ArchivedDate,
        CanEdit = CanEdit,
        CanViewPassword = CanViewPassword,
        CanDelete = CanDelete,
        CanRestore = CanRestore,
        Login = Login,
        Card = Card,
        Identity = Identity,
        SecureNote = SecureNote,
        Ssh = Ssh,
        BankAccount = BankAccount,
        DriversLicense = DriversLicense,
        Passport = Passport,
        Fields = Fields,
        Attachments = Attachments,
        PasswordHistory = PasswordHistory,
    };

    // 返回一个仅 DeletedDate 不同的副本(软删除/恢复用)。新增属性时须同步此拷贝。
    public Cipher WithDeletedDate(DateTimeOffset? deletedDate) => new()
    {
        Id = Id,
        Type = Type,
        OrganizationId = OrganizationId,
        CollectionIds = CollectionIds,
        FolderId = FolderId,
        Key = Key,
        UsesBlobEncryption = UsesBlobEncryption,
        Favorite = Favorite,
        Reprompt = Reprompt,
        Name = Name,
        Notes = Notes,
        CreationDate = CreationDate,
        RevisionDate = RevisionDate,
        DeletedDate = deletedDate,
        ArchivedDate = ArchivedDate,
        CanEdit = CanEdit,
        CanViewPassword = CanViewPassword,
        CanDelete = CanDelete,
        CanRestore = CanRestore,
        Login = Login,
        Card = Card,
        Identity = Identity,
        SecureNote = SecureNote,
        Ssh = Ssh,
        BankAccount = BankAccount,
        DriversLicense = DriversLicense,
        Passport = Passport,
        Fields = Fields,
        Attachments = Attachments,
        PasswordHistory = PasswordHistory,
    };

    public Cipher WithArchivedDate(DateTimeOffset? archivedDate) => new()
    {
        Id = Id,
        Type = Type,
        OrganizationId = OrganizationId,
        CollectionIds = CollectionIds,
        FolderId = FolderId,
        Key = Key,
        UsesBlobEncryption = UsesBlobEncryption,
        Favorite = Favorite,
        Reprompt = Reprompt,
        Name = Name,
        Notes = Notes,
        CreationDate = CreationDate,
        RevisionDate = RevisionDate,
        DeletedDate = DeletedDate,
        ArchivedDate = archivedDate,
        CanEdit = CanEdit,
        CanViewPassword = CanViewPassword,
        CanDelete = CanDelete,
        CanRestore = CanRestore,
        Login = Login,
        Card = Card,
        Identity = Identity,
        SecureNote = SecureNote,
        Ssh = Ssh,
        BankAccount = BankAccount,
        DriversLicense = DriversLicense,
        Passport = Passport,
        Fields = Fields,
        Attachments = Attachments,
        PasswordHistory = PasswordHistory,
    };

    public Cipher WithFavorite(bool favorite) => new()
    {
        Id = Id,
        Type = Type,
        OrganizationId = OrganizationId,
        CollectionIds = CollectionIds,
        FolderId = FolderId,
        Key = Key,
        UsesBlobEncryption = UsesBlobEncryption,
        Favorite = favorite,
        Reprompt = Reprompt,
        Name = Name,
        Notes = Notes,
        CreationDate = CreationDate,
        RevisionDate = RevisionDate,
        DeletedDate = DeletedDate,
        ArchivedDate = ArchivedDate,
        CanEdit = CanEdit,
        CanViewPassword = CanViewPassword,
        CanDelete = CanDelete,
        CanRestore = CanRestore,
        Login = Login,
        Card = Card,
        Identity = Identity,
        SecureNote = SecureNote,
        Ssh = Ssh,
        BankAccount = BankAccount,
        DriversLicense = DriversLicense,
        Passport = Passport,
        Fields = Fields,
        Attachments = Attachments,
        PasswordHistory = PasswordHistory,
    };

    // 返回一个 Id/CreationDate/RevisionDate 不同的副本(新建条目落库后回填服务端元数据用)。新增属性时须同步此拷贝。
    public Cipher WithMetadata(string id, DateTimeOffset creationDate, DateTimeOffset revisionDate) => new()
    {
        Id = id,
        Type = Type,
        OrganizationId = OrganizationId,
        CollectionIds = CollectionIds,
        FolderId = FolderId,
        Key = Key,
        UsesBlobEncryption = UsesBlobEncryption,
        Favorite = Favorite,
        Reprompt = Reprompt,
        Name = Name,
        Notes = Notes,
        CreationDate = creationDate,
        RevisionDate = revisionDate,
        DeletedDate = DeletedDate,
        ArchivedDate = ArchivedDate,
        CanEdit = CanEdit,
        CanViewPassword = CanViewPassword,
        CanDelete = CanDelete,
        CanRestore = CanRestore,
        Login = Login,
        Card = Card,
        Identity = Identity,
        SecureNote = SecureNote,
        Ssh = Ssh,
        BankAccount = BankAccount,
        DriversLicense = DriversLicense,
        Passport = Passport,
        Fields = Fields,
        Attachments = Attachments,
        PasswordHistory = PasswordHistory,
    };
}

public sealed record PasswordHistoryEntry(string Password, DateTimeOffset? LastUsedDate);

public sealed record CipherLogin(string? Username, string? Password, string? Totp, IReadOnlyList<CipherLoginUri> Uris)
{
    public DateTimeOffset? PasswordRevisionDate { get; init; }
    public bool? AutofillOnPageLoad { get; init; }
    public IReadOnlyList<CipherFido2Credential> Fido2Credentials { get; init; } = Array.Empty<CipherFido2Credential>();
    public bool HasFido2Credentials => Fido2Credentials.Count > 0;
}

public sealed record CipherLoginUri(string? Uri, int? Match);

public sealed record CipherFido2Credential(
    string? CredentialId,
    string? KeyType,
    string? KeyAlgorithm,
    string? KeyCurve,
    string? KeyValue,
    string? RpId,
    string? UserHandle,
    string? UserName,
    long? Counter,
    string? RpName,
    string? UserDisplayName,
    bool Discoverable,
    DateTimeOffset? CreationDate);

public sealed record CipherCard(
    string? CardholderName,
    string? Number,
    string? ExpMonth,
    string? ExpYear,
    string? Code,
    string? Brand);

public sealed record CipherIdentity(
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

public sealed record CipherSecureNote(int Type);

public sealed record CipherSsh(string? PrivateKey, string? PublicKey, string? Fingerprint);

public sealed record CipherBankAccount(
    string? BankName,
    string? NameOnAccount,
    string? AccountType,
    string? AccountNumber,
    string? RoutingNumber,
    string? BranchNumber,
    string? Pin,
    string? SwiftCode,
    string? Iban,
    string? BankContactPhone);

public sealed record CipherDriversLicense(
    string? FirstName,
    string? MiddleName,
    string? LastName,
    string? DateOfBirth,
    string? LicenseNumber,
    string? IssuingCountry,
    string? IssuingState,
    string? IssueDate,
    string? ExpirationDate,
    string? IssuingAuthority,
    string? LicenseClass);

public sealed record CipherPassport(
    string? Surname,
    string? GivenName,
    string? DateOfBirth,
    string? Sex,
    string? BirthPlace,
    string? Nationality,
    string? IssuingCountry,
    string? PassportNumber,
    string? PassportType,
    string? NationalIdentificationNumber,
    string? IssuingAuthority,
    string? IssueDate,
    string? ExpirationDate);

public sealed record CipherField(
    string Name,
    string? Value,
    CipherFieldType Type,
    int? LinkedId = null,
    int? RawType = null)
{
    public int WireType => RawType ?? (int)Type;

    public static CipherField FromWire(
        string name,
        string? value,
        int wireType,
        int? linkedId = null) => wireType switch
    {
        (int)CipherFieldType.Text => new(name, value, CipherFieldType.Text, linkedId),
        (int)CipherFieldType.Hidden => new(name, value, CipherFieldType.Hidden, linkedId),
        (int)CipherFieldType.Boolean => new(name, value, CipherFieldType.Boolean, linkedId),
        (int)CipherFieldType.Linked => new(name, value, CipherFieldType.Linked, linkedId),
        _ => new(name, value, CipherFieldType.Unsupported, linkedId, wireType),
    };
}

public enum CipherFieldType
{
    Unsupported = -1,
    Text = 0,
    Hidden = 1,
    Boolean = 2,
    Linked = 3,
}
