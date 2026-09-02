using System.Text.Json.Serialization;

namespace Vault;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class VersionedCipherBlobWire
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("content")]
    public CipherBlobV1Wire? Content { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class CipherBlobV1Wire
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("notes")] public string? Notes { get; init; }
    [JsonPropertyName("typeData")] public CipherTypeDataWire? TypeData { get; init; }

    [JsonPropertyName("fields")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FieldDataWire[]? Fields { get; init; }

    [JsonPropertyName("passwordHistory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PasswordHistoryDataWire[]? PasswordHistory { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LoginDataWire), "login")]
[JsonDerivedType(typeof(CardDataWire), "card")]
[JsonDerivedType(typeof(IdentityDataWire), "identity")]
[JsonDerivedType(typeof(SecureNoteDataWire), "secureNote")]
[JsonDerivedType(typeof(SshKeyDataWire), "sshKey")]
[JsonDerivedType(typeof(BankAccountDataWire), "bankAccount")]
[JsonDerivedType(typeof(PassportDataWire), "passport")]
[JsonDerivedType(typeof(DriversLicenseDataWire), "driversLicense")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal abstract class CipherTypeDataWire;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class LoginDataWire : CipherTypeDataWire
{
    [JsonPropertyName("username")] public string? Username { get; init; }
    [JsonPropertyName("password")] public string? Password { get; init; }
    [JsonPropertyName("passwordRevisionDate")] public DateTimeOffset? PasswordRevisionDate { get; init; }
    [JsonPropertyName("uris")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LoginUriDataWire[]? Uris { get; init; }
    [JsonPropertyName("totp")] public string? Totp { get; init; }
    [JsonPropertyName("autofillOnPageLoad")] public bool? AutofillOnPageLoad { get; init; }
    [JsonPropertyName("fido2Credentials")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Fido2CredentialDataWire[]? Fido2Credentials { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class LoginUriDataWire
{
    [JsonPropertyName("uri")] public string? Uri { get; init; }
    [JsonPropertyName("match")] public int? Match { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class Fido2CredentialDataWire
{
    [JsonPropertyName("credentialId")] public string? CredentialId { get; init; }
    [JsonPropertyName("keyType")] public string? KeyType { get; init; }
    [JsonPropertyName("keyAlgorithm")] public string? KeyAlgorithm { get; init; }
    [JsonPropertyName("keyCurve")] public string? KeyCurve { get; init; }
    [JsonPropertyName("keyValue")] public string? KeyValue { get; init; }
    [JsonPropertyName("rpId")] public string? RpId { get; init; }
    [JsonPropertyName("userHandle")] public string? UserHandle { get; init; }
    [JsonPropertyName("userName")] public string? UserName { get; init; }
    [JsonRequired]
    [JsonPropertyName("counter")] public ulong Counter { get; init; }
    [JsonPropertyName("rpName")] public string? RpName { get; init; }
    [JsonPropertyName("userDisplayName")] public string? UserDisplayName { get; init; }
    [JsonRequired]
    [JsonPropertyName("discoverable")] public bool Discoverable { get; init; }
    [JsonRequired]
    [JsonPropertyName("creationDate")] public DateTimeOffset CreationDate { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class CardDataWire : CipherTypeDataWire
{
    [JsonPropertyName("cardholderName")] public string? CardholderName { get; init; }
    [JsonPropertyName("expMonth")] public string? ExpMonth { get; init; }
    [JsonPropertyName("expYear")] public string? ExpYear { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("brand")] public string? Brand { get; init; }
    [JsonPropertyName("number")] public string? Number { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class IdentityDataWire : CipherTypeDataWire
{
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("firstName")] public string? FirstName { get; init; }
    [JsonPropertyName("middleName")] public string? MiddleName { get; init; }
    [JsonPropertyName("lastName")] public string? LastName { get; init; }
    [JsonPropertyName("address1")] public string? Address1 { get; init; }
    [JsonPropertyName("address2")] public string? Address2 { get; init; }
    [JsonPropertyName("address3")] public string? Address3 { get; init; }
    [JsonPropertyName("city")] public string? City { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("postalCode")] public string? PostalCode { get; init; }
    [JsonPropertyName("country")] public string? Country { get; init; }
    [JsonPropertyName("company")] public string? Company { get; init; }
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("phone")] public string? Phone { get; init; }
    [JsonPropertyName("ssn")] public string? Ssn { get; init; }
    [JsonPropertyName("username")] public string? Username { get; init; }
    [JsonPropertyName("passportNumber")] public string? PassportNumber { get; init; }
    [JsonPropertyName("licenseNumber")] public string? LicenseNumber { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SecureNoteDataWire : CipherTypeDataWire
{
    [JsonRequired]
    [JsonPropertyName("secureNoteType")] public int SecureNoteType { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SshKeyDataWire : CipherTypeDataWire
{
    [JsonPropertyName("privateKey")] public string? PrivateKey { get; init; }
    [JsonPropertyName("publicKey")] public string? PublicKey { get; init; }
    [JsonPropertyName("fingerprint")] public string? Fingerprint { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class BankAccountDataWire : CipherTypeDataWire
{
    [JsonPropertyName("bankName")] public string? BankName { get; init; }
    [JsonPropertyName("nameOnAccount")] public string? NameOnAccount { get; init; }
    [JsonPropertyName("accountType")] public string? AccountType { get; init; }
    [JsonPropertyName("accountNumber")] public string? AccountNumber { get; init; }
    [JsonPropertyName("routingNumber")] public string? RoutingNumber { get; init; }
    [JsonPropertyName("branchNumber")] public string? BranchNumber { get; init; }
    [JsonPropertyName("pin")] public string? Pin { get; init; }
    [JsonPropertyName("swiftCode")] public string? SwiftCode { get; init; }
    [JsonPropertyName("iban")] public string? Iban { get; init; }
    [JsonPropertyName("bankContactPhone")] public string? BankContactPhone { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class DriversLicenseDataWire : CipherTypeDataWire
{
    [JsonPropertyName("firstName")] public string? FirstName { get; init; }
    [JsonPropertyName("middleName")] public string? MiddleName { get; init; }
    [JsonPropertyName("lastName")] public string? LastName { get; init; }
    [JsonPropertyName("dateOfBirth")] public string? DateOfBirth { get; init; }
    [JsonPropertyName("licenseNumber")] public string? LicenseNumber { get; init; }
    [JsonPropertyName("issuingCountry")] public string? IssuingCountry { get; init; }
    [JsonPropertyName("issuingState")] public string? IssuingState { get; init; }
    [JsonPropertyName("issueDate")] public string? IssueDate { get; init; }
    [JsonPropertyName("expirationDate")] public string? ExpirationDate { get; init; }
    [JsonPropertyName("issuingAuthority")] public string? IssuingAuthority { get; init; }
    [JsonPropertyName("licenseClass")] public string? LicenseClass { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class PassportDataWire : CipherTypeDataWire
{
    [JsonPropertyName("surname")] public string? Surname { get; init; }
    [JsonPropertyName("givenName")] public string? GivenName { get; init; }
    [JsonPropertyName("dateOfBirth")] public string? DateOfBirth { get; init; }
    [JsonPropertyName("sex")] public string? Sex { get; init; }
    [JsonPropertyName("birthPlace")] public string? BirthPlace { get; init; }
    [JsonPropertyName("nationality")] public string? Nationality { get; init; }
    [JsonPropertyName("issuingCountry")] public string? IssuingCountry { get; init; }
    [JsonPropertyName("passportNumber")] public string? PassportNumber { get; init; }
    [JsonPropertyName("passportType")] public string? PassportType { get; init; }
    [JsonPropertyName("nationalIdentificationNumber")] public string? NationalIdentificationNumber { get; init; }
    [JsonPropertyName("issuingAuthority")] public string? IssuingAuthority { get; init; }
    [JsonPropertyName("issueDate")] public string? IssueDate { get; init; }
    [JsonPropertyName("expirationDate")] public string? ExpirationDate { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class FieldDataWire
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("value")] public string? Value { get; init; }
    [JsonRequired]
    [JsonPropertyName("type")] public int Type { get; init; }
    [JsonPropertyName("linkedId")] public int? LinkedId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class PasswordHistoryDataWire
{
    [JsonPropertyName("password")] public string? Password { get; init; }
    [JsonRequired]
    [JsonPropertyName("lastUsedDate")] public DateTimeOffset LastUsedDate { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(VersionedCipherBlobWire))]
internal partial class CipherBlobJsonContext : JsonSerializerContext;
