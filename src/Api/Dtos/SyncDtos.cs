using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Enums;

namespace Api.Dtos;

public sealed record SyncResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("profile")] ProfileDto? Profile = null,
    [property: JsonPropertyName("folders")] FolderDto[]? Folders = null,
    [property: JsonPropertyName("ciphers")] CipherDto[]? Ciphers = null,
    [property: JsonPropertyName("collections")] CollectionDto[]? Collections = null,
    [property: JsonPropertyName("policies")] OrganizationPolicyDto[]? Policies = null,
    [property: JsonPropertyName("sends")] SendResponseDto[]? Sends = null,
    [property: JsonPropertyName("domains")] DomainsDto? Domains = null,
    [property: JsonPropertyName("policiesNew")] OrganizationPolicyDto[]? PoliciesNew = null);

public sealed record ProfileDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("privateKey")] string? PrivateKey,
    [property: JsonPropertyName("organizations")] ProfileOrganizationDto[]? Organizations = null)
{
    [JsonPropertyName("premium")]
    public bool? Premium { get; init; }

    [JsonPropertyName("premiumFromOrganization")]
    public bool? PremiumFromOrganization { get; init; }

    [JsonPropertyName("accountKeys")]
    public JsonElement? AccountKeys { get; init; }

    [JsonPropertyName("providers")]
    public ProfileProviderDto[]? Providers { get; init; }

    [JsonPropertyName("providerOrganizations")]
    public ProfileProviderOrganizationDto[]? ProviderOrganizations { get; init; }

    [JsonPropertyName("organizationsNew")]
    public ProfileOrganizationDto[]? OrganizationsNew { get; init; }

    [JsonPropertyName("securityStamp")]
    public string? SecurityStamp { get; init; }

    [JsonPropertyName("forcePasswordReset")]
    public bool? ForcePasswordReset { get; init; }

    [JsonPropertyName("verifyDevices")]
    public bool? VerifyDevices { get; init; }

    [JsonPropertyName("usesKeyConnector")]
    public bool? UsesKeyConnector { get; init; }
}

public sealed record ProfileOrganizationDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("providerId")] string? ProviderId = null,
    [property: JsonPropertyName("usePolicies")] bool UsePolicies = true,
    [property: JsonPropertyName("productTierType")] int? ProductTierType = null,
    [property: JsonPropertyName("userIsClaimedByOrganization")] bool? UserIsClaimedByOrganization = null);

public sealed record ProfileProviderDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("key")] string? Key);

public sealed record ProfileProviderOrganizationDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("providerId")] string? ProviderId,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("status")] int Status = 0,
    [property: JsonPropertyName("type")] int Type = 0,
    [property: JsonPropertyName("enabled")] bool Enabled = true);

public sealed record DomainsDto(
    [property: JsonPropertyName("equivalentDomains")] string[][]? EquivalentDomains,
    [property: JsonPropertyName("globalEquivalentDomains")] GlobalEquivalentDomainDto[]? GlobalEquivalentDomains,
    [property: JsonPropertyName("object")] string? Object = null);

public sealed record GlobalEquivalentDomainDto(
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("domains")] string[]? Domains,
    [property: JsonPropertyName("excluded")] bool Excluded);

public sealed record CollectionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("organizationId")] string OrganizationId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("readOnly")] bool ReadOnly,
    [property: JsonPropertyName("hidePasswords")] bool HidePasswords,
    [property: JsonPropertyName("manage")] bool Manage,
    [property: JsonPropertyName("externalId")] string? ExternalId = null);

public sealed record OrganizationPolicyDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("organizationId")] string OrganizationId,
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("data")] JsonElement? Data,
    [property: JsonPropertyName("revisionDate")] DateTimeOffset? RevisionDate = null,
    [property: JsonPropertyName("canToggleState")] bool? CanToggleState = null);

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
    [property: JsonPropertyName("deletedDate")] DateTimeOffset? DeletedDate,
    [property: JsonPropertyName("attachments")] CipherAttachmentDto[]? Attachments = null,
    [property: JsonPropertyName("passwordHistory")] PasswordHistoryDto[]? PasswordHistory = null,
    [property: JsonPropertyName("collectionIds")] string[]? CollectionIds = null,
    [property: JsonPropertyName("edit")] bool? Edit = null,
    [property: JsonPropertyName("viewPassword")] bool? ViewPassword = null,
    [property: JsonPropertyName("permissions")] CipherPermissionsDto? Permissions = null,
    [property: JsonPropertyName("archivedDate")] DateTimeOffset? ArchivedDate = null,
    [property: JsonPropertyName("bankAccount")] BankAccountDto? BankAccount = null,
    [property: JsonPropertyName("driversLicense")] DriversLicenseDto? DriversLicense = null,
    [property: JsonPropertyName("passport")] PassportDto? Passport = null,
    [property: JsonPropertyName("data")] string? Data = null);

public sealed record CipherPermissionsDto(
    [property: JsonPropertyName("delete")] bool Delete,
    [property: JsonPropertyName("restore")] bool Restore);

public sealed record PasswordHistoryDto(
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("lastUsedDate")] string? LastUsedDate);

public sealed record LoginDto(
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("totp")] string? Totp,
    [property: JsonPropertyName("uris")] LoginUriDto[]? Uris,
    [property: JsonPropertyName("fido2Credentials")] Fido2CredentialDto[]? Fido2Credentials = null,
    [property: JsonPropertyName("passwordRevisionDate")] DateTimeOffset? PasswordRevisionDate = null,
    [property: JsonPropertyName("autofillOnPageLoad")] bool? AutofillOnPageLoad = null);

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

public sealed record BankAccountDto(
    [property: JsonPropertyName("bankName")] string? BankName,
    [property: JsonPropertyName("nameOnAccount")] string? NameOnAccount,
    [property: JsonPropertyName("accountType")] string? AccountType,
    [property: JsonPropertyName("accountNumber")] string? AccountNumber,
    [property: JsonPropertyName("routingNumber")] string? RoutingNumber,
    [property: JsonPropertyName("branchNumber")] string? BranchNumber,
    [property: JsonPropertyName("pin")] string? Pin,
    [property: JsonPropertyName("swiftCode")] string? SwiftCode,
    [property: JsonPropertyName("iban")] string? Iban,
    [property: JsonPropertyName("bankContactPhone")] string? BankContactPhone);

public sealed record DriversLicenseDto(
    [property: JsonPropertyName("firstName")] string? FirstName,
    [property: JsonPropertyName("middleName")] string? MiddleName,
    [property: JsonPropertyName("lastName")] string? LastName,
    [property: JsonPropertyName("dateOfBirth")] string? DateOfBirth,
    [property: JsonPropertyName("licenseNumber")] string? LicenseNumber,
    [property: JsonPropertyName("issuingCountry")] string? IssuingCountry,
    [property: JsonPropertyName("issuingState")] string? IssuingState,
    [property: JsonPropertyName("issueDate")] string? IssueDate,
    [property: JsonPropertyName("expirationDate")] string? ExpirationDate,
    [property: JsonPropertyName("issuingAuthority")] string? IssuingAuthority,
    [property: JsonPropertyName("licenseClass")] string? LicenseClass);

public sealed record PassportDto(
    [property: JsonPropertyName("surname")] string? Surname,
    [property: JsonPropertyName("givenName")] string? GivenName,
    [property: JsonPropertyName("dateOfBirth")] string? DateOfBirth,
    [property: JsonPropertyName("sex")] string? Sex,
    [property: JsonPropertyName("birthPlace")] string? BirthPlace,
    [property: JsonPropertyName("nationality")] string? Nationality,
    [property: JsonPropertyName("issuingCountry")] string? IssuingCountry,
    [property: JsonPropertyName("passportNumber")] string? PassportNumber,
    [property: JsonPropertyName("passportType")] string? PassportType,
    [property: JsonPropertyName("nationalIdentificationNumber")] string? NationalIdentificationNumber,
    [property: JsonPropertyName("issuingAuthority")] string? IssuingAuthority,
    [property: JsonPropertyName("issueDate")] string? IssueDate,
    [property: JsonPropertyName("expirationDate")] string? ExpirationDate);

public sealed record FieldDto(
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("linkedId")] int? LinkedId = null);

// Vaultwarden Attachment.to_json emits size as a JSON string for mobile compatibility.
// Conversion to a number is deliberately left to the Vault layer.
// Legacy attachments have a null key and encrypt both name and payload with the item key.
public sealed record CipherAttachmentDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("fileName")] string? FileName,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("size")] string? Size,
    [property: JsonPropertyName("sizeName")] string? SizeName,
    [property: JsonPropertyName("url")] string? Url);

// POST /api/ciphers/{cipherId}/attachment/v2 request body (camelCase JSON).
// key is Encrypt(attKey.FullKey, itemKey); fileSize is the encrypted payload length.
// Older Vaultwarden releases ignore the revision field; callers must never retry without it.
public sealed record AttachmentUploadRequest(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("fileSize")] long FileSize,
    [property: JsonPropertyName("lastKnownRevisionDate")] DateTime LastKnownRevisionDate);

// POST /api/ciphers/{cipherId}/attachment/v2 response. url may be relative to /api.
// The server's accompanying cipherResponse is intentionally ignored.
public sealed record AttachmentUploadV2Response(
    [property: JsonPropertyName("attachmentId")] string AttachmentId,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("fileUploadType")] FileUploadType FileUploadType);
