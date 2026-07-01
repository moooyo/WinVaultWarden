using Core.Enums;

namespace Core.Models;

// 解密后的密码库条目领域模型。明文字段只允许存在于内存中。
public sealed class Cipher
{
    public string Id { get; init; } = string.Empty;
    public CipherType Type { get; init; }
    public string? OrganizationId { get; init; }
    public string? FolderId { get; init; }
    public bool Favorite { get; init; }
    public bool Reprompt { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Notes { get; init; }
    public DateTimeOffset CreationDate { get; init; }
    public DateTimeOffset RevisionDate { get; init; }
    public DateTimeOffset? DeletedDate { get; init; }
    public bool IsDeleted => DeletedDate is not null;
    public CipherLogin? Login { get; init; }
    public CipherCard? Card { get; init; }
    public CipherIdentity? Identity { get; init; }
    public CipherSecureNote? SecureNote { get; init; }
    public CipherSsh? Ssh { get; init; }
    public IReadOnlyList<CipherField> Fields { get; init; } = Array.Empty<CipherField>();
    public IReadOnlyList<CipherAttachment> Attachments { get; init; } = Array.Empty<CipherAttachment>();
    public IReadOnlyList<PasswordHistoryEntry> PasswordHistory { get; init; } = Array.Empty<PasswordHistoryEntry>();
}

public sealed record PasswordHistoryEntry(string Password, DateTimeOffset? LastUsedDate);

public sealed record CipherLogin(string? Username, string? Password, string? Totp, IReadOnlyList<CipherLoginUri> Uris)
{
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

public sealed record CipherField(string Name, string? Value, CipherFieldType Type);

public enum CipherFieldType
{
    Text = 0,
    Hidden = 1,
    Boolean = 2,
}
