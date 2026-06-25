using System.Text.Json.Serialization;

namespace Api.Dtos;

public sealed record SyncResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("profile")] ProfileDto? Profile = null,
    [property: JsonPropertyName("folders")] FolderDto[]? Folders = null,
    [property: JsonPropertyName("ciphers")] CipherDto[]? Ciphers = null);

public sealed record ProfileDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("privateKey")] string? PrivateKey);

public sealed record FolderDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("revisionDate")] DateTimeOffset? RevisionDate);

public sealed record CipherDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("organizationId")] string? OrganizationId,
    [property: JsonPropertyName("folderId")] string? FolderId,
    [property: JsonPropertyName("favorite")] bool Favorite,
    [property: JsonPropertyName("reprompt")] int Reprompt,
    [property: JsonPropertyName("login")] LoginDto? Login,
    [property: JsonPropertyName("card")] CardDto? Card,
    [property: JsonPropertyName("identity")] IdentityDto? Identity,
    [property: JsonPropertyName("secureNote")] SecureNoteDto? SecureNote,
    [property: JsonPropertyName("sshKey")] SshKeyDto? SshKey,
    [property: JsonPropertyName("fields")] FieldDto[]? Fields,
    [property: JsonPropertyName("creationDate")] DateTimeOffset? CreationDate,
    [property: JsonPropertyName("revisionDate")] DateTimeOffset? RevisionDate,
    [property: JsonPropertyName("deletedDate")] DateTimeOffset? DeletedDate);

public sealed record LoginDto(
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("totp")] string? Totp,
    [property: JsonPropertyName("uris")] LoginUriDto[]? Uris,
    [property: JsonPropertyName("fido2Credentials")] Fido2CredentialDto[]? Fido2Credentials = null);

public sealed record LoginUriDto(
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("match")] int? Match);

public sealed record Fido2CredentialDto(
    [property: JsonPropertyName("credentialId")] string? CredentialId,
    [property: JsonPropertyName("keyType")] string? KeyType,
    [property: JsonPropertyName("keyAlgorithm")] string? KeyAlgorithm,
    [property: JsonPropertyName("keyCurve")] string? KeyCurve,
    [property: JsonPropertyName("keyValue")] string? KeyValue,
    [property: JsonPropertyName("rpId")] string? RpId,
    [property: JsonPropertyName("userHandle")] string? UserHandle,
    [property: JsonPropertyName("userName")] string? UserName,
    [property: JsonPropertyName("counter")] string? Counter,
    [property: JsonPropertyName("rpName")] string? RpName,
    [property: JsonPropertyName("userDisplayName")] string? UserDisplayName,
    [property: JsonPropertyName("discoverable")] string? Discoverable,
    [property: JsonPropertyName("creationDate")] DateTimeOffset? CreationDate);

public sealed record CardDto(
    [property: JsonPropertyName("cardholderName")] string? CardholderName,
    [property: JsonPropertyName("number")] string? Number,
    [property: JsonPropertyName("expMonth")] string? ExpMonth,
    [property: JsonPropertyName("expYear")] string? ExpYear,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("brand")] string? Brand);

public sealed record IdentityDto(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("firstName")] string? FirstName,
    [property: JsonPropertyName("middleName")] string? MiddleName,
    [property: JsonPropertyName("lastName")] string? LastName,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("company")] string? Company,
    [property: JsonPropertyName("ssn")] string? Ssn,
    [property: JsonPropertyName("passportNumber")] string? PassportNumber,
    [property: JsonPropertyName("licenseNumber")] string? LicenseNumber,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("phone")] string? Phone,
    [property: JsonPropertyName("address1")] string? Address1,
    [property: JsonPropertyName("address2")] string? Address2,
    [property: JsonPropertyName("address3")] string? Address3,
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("postalCode")] string? PostalCode,
    [property: JsonPropertyName("country")] string? Country);

public sealed record SecureNoteDto([property: JsonPropertyName("type")] int Type);

public sealed record SshKeyDto(
    [property: JsonPropertyName("privateKey")] string? PrivateKey,
    [property: JsonPropertyName("publicKey")] string? PublicKey,
    [property: JsonPropertyName("keyFingerprint")] string? KeyFingerprint);

public sealed record FieldDto(
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("value")] string? Value);
