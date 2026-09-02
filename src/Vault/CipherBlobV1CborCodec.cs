using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text.Json;
using Core.Enums;
using Core.Models;
using Core.Passkeys;

namespace Vault;

internal sealed record CipherBlobContent(
    CipherType Type,
    string Name,
    string? Notes,
    CipherLogin? Login,
    CipherCard? Card,
    CipherIdentity? Identity,
    CipherSecureNote? SecureNote,
    CipherSsh? Ssh,
    CipherBankAccount? BankAccount,
    CipherDriversLicense? DriversLicense,
    CipherPassport? Passport,
    IReadOnlyList<CipherField> Fields,
    IReadOnlyList<PasswordHistoryEntry> PasswordHistory);

internal static class CipherBlobV1CborCodec
{
    private const int MaximumBlobContentBytes = 4 * 1024 * 1024;
    private static readonly CipherBlobJsonContext JsonContext = new(
        new JsonSerializerOptions
        {
            AllowOutOfOrderMetadataProperties = true,
        });

    internal static byte[] Encode(Cipher cipher)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        var wire = ToWire(cipher);
        var json = JsonSerializer.SerializeToUtf8Bytes(
            wire,
            JsonContext.VersionedCipherBlobWire);
        try
        {
            using var document = JsonDocument.Parse(json);
            // Ciborium preserves serde field order. In particular, the internally tagged
            // type discriminator is emitted before variant fields.
            var writer = new CborWriter(CborConformanceMode.Lax);
            WriteJsonValue(writer, document.RootElement);
            var encoded = writer.Encode();
            if (encoded.Length > MaximumBlobContentBytes)
                throw new InvalidDataException("Cipher blob content exceeds the size limit.");
            return encoded;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    internal static CipherBlobContent Decode(ReadOnlyMemory<byte> encoded)
    {
        if (encoded.IsEmpty || encoded.Length > MaximumBlobContentBytes)
            throw new InvalidDataException("Cipher blob content is empty or exceeds the size limit.");

        var reader = new CborReader(
            encoded,
            CborConformanceMode.Lax,
            allowMultipleRootLevelValues: false);
        using var stream = new MemoryStream();
        byte[]? json = null;
        try
        {
            using (var writer = new Utf8JsonWriter(stream))
            {
                ReadCborValue(reader, writer, 0);
                writer.Flush();
            }
            if (reader.BytesRemaining != 0 || reader.PeekState() != CborReaderState.Finished)
                throw new InvalidDataException("Cipher blob content contains trailing CBOR data.");

            json = stream.ToArray();
            try
            {
                var wire = JsonSerializer.Deserialize(
                    json,
                    JsonContext.VersionedCipherBlobWire)
                    ?? throw new InvalidDataException("Cipher blob content is null.");
                return FromWire(wire);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                throw new InvalidDataException(
                    "Cipher blob content has an invalid schema.",
                    exception);
            }
        }
        finally
        {
            if (json is not null)
                CryptographicOperations.ZeroMemory(json);
            if (stream.TryGetBuffer(out var buffer))
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, checked((int)stream.Length)));
        }
    }

    private static VersionedCipherBlobWire ToWire(Cipher cipher)
    {
        CipherTypeDataWire typeData = cipher.Type switch
        {
            CipherType.Login => ToWire(cipher.Login ?? throw MissingTypeData(cipher.Type)),
            CipherType.Card => ToWire(cipher.Card ?? throw MissingTypeData(cipher.Type)),
            CipherType.Identity => ToWire(cipher.Identity ?? throw MissingTypeData(cipher.Type)),
            CipherType.SecureNote => new SecureNoteDataWire
            {
                SecureNoteType = (cipher.SecureNote ?? throw MissingTypeData(cipher.Type)).Type,
            },
            CipherType.SshKey => ToWire(cipher.Ssh ?? throw MissingTypeData(cipher.Type)),
            CipherType.BankAccount => ToWire(cipher.BankAccount ?? throw MissingTypeData(cipher.Type)),
            CipherType.DriversLicense => ToWire(cipher.DriversLicense ?? throw MissingTypeData(cipher.Type)),
            CipherType.Passport => ToWire(cipher.Passport ?? throw MissingTypeData(cipher.Type)),
            _ => throw new InvalidDataException(
                $"Cipher type {(int)cipher.Type} cannot be represented by blob version 1."),
        };

        var fields = cipher.Fields.Count == 0
            ? null
            : cipher.Fields.Select(field =>
            {
                if (field.WireType is < 0 or > 3)
                    throw new InvalidDataException(
                        "Unsupported custom fields cannot be written to a cipher blob.");
                return new FieldDataWire
                {
                    Name = field.Name,
                    Value = field.Value,
                    Type = field.WireType,
                    LinkedId = field.LinkedId,
                };
            }).ToArray();

        var history = cipher.PasswordHistory.Count == 0
            ? null
            : cipher.PasswordHistory.Select(entry => new PasswordHistoryDataWire
            {
                Password = entry.Password,
                LastUsedDate = entry.LastUsedDate
                    ?? throw new InvalidDataException(
                        "Password history without a timestamp cannot be written to a cipher blob."),
            }).ToArray();

        return new VersionedCipherBlobWire
        {
            Version = "1",
            Content = new CipherBlobV1Wire
            {
                Name = cipher.Name,
                Notes = cipher.Notes,
                TypeData = typeData,
                Fields = fields,
                PasswordHistory = history,
            },
        };
    }

    private static CipherBlobContent FromWire(VersionedCipherBlobWire wire)
    {
        if (wire.Version != "1" || wire.Content is null || wire.Content.TypeData is null)
            throw new InvalidDataException("Unsupported or incomplete cipher blob version.");
        var content = wire.Content;
        if (content.Name is null)
            throw new InvalidDataException("Cipher blob name is missing.");

        var fields = (content.Fields ?? Array.Empty<FieldDataWire>())
            .Select(field =>
            {
                if (field.Type is < 0 or > 3)
                    throw new InvalidDataException(
                        "Cipher blob contains an unsupported custom field type.");
                return CipherField.FromWire(
                    field.Name ?? string.Empty,
                    field.Value,
                    field.Type,
                    field.LinkedId);
            })
            .ToArray();
        var history = (content.PasswordHistory ?? Array.Empty<PasswordHistoryDataWire>())
            .Select(entry => new PasswordHistoryEntry(
                entry.Password
                    ?? throw new InvalidDataException("Cipher blob password history is incomplete."),
                entry.LastUsedDate))
            .ToArray();

        return content.TypeData switch
        {
            LoginDataWire login => NewContent(
                CipherType.Login, content, login: FromWire(login), fields: fields, history: history),
            CardDataWire card => NewContent(
                CipherType.Card,
                content,
                card: new CipherCard(
                    card.CardholderName,
                    card.Number,
                    card.ExpMonth,
                    card.ExpYear,
                    card.Code,
                    card.Brand),
                fields: fields,
                history: history),
            IdentityDataWire identity => NewContent(
                CipherType.Identity, content, identity: FromWire(identity), fields: fields, history: history),
            SecureNoteDataWire note => NewContent(
                CipherType.SecureNote,
                content,
                secureNote: new CipherSecureNote(note.SecureNoteType),
                fields: fields,
                history: history),
            SshKeyDataWire ssh => NewContent(
                CipherType.SshKey,
                content,
                ssh: new CipherSsh(
                    Require(ssh.PrivateKey, "SSH private key"),
                    Require(ssh.PublicKey, "SSH public key"),
                    Require(ssh.Fingerprint, "SSH fingerprint")),
                fields: fields,
                history: history),
            BankAccountDataWire bank => NewContent(
                CipherType.BankAccount, content, bankAccount: FromWire(bank), fields: fields, history: history),
            DriversLicenseDataWire license => NewContent(
                CipherType.DriversLicense,
                content,
                driversLicense: FromWire(license),
                fields: fields,
                history: history),
            PassportDataWire passport => NewContent(
                CipherType.Passport, content, passport: FromWire(passport), fields: fields, history: history),
            _ => throw new InvalidDataException("Cipher blob contains an unsupported type payload."),
        };
    }

    private static CipherBlobContent NewContent(
        CipherType type,
        CipherBlobV1Wire content,
        CipherLogin? login = null,
        CipherCard? card = null,
        CipherIdentity? identity = null,
        CipherSecureNote? secureNote = null,
        CipherSsh? ssh = null,
        CipherBankAccount? bankAccount = null,
        CipherDriversLicense? driversLicense = null,
        CipherPassport? passport = null,
        IReadOnlyList<CipherField>? fields = null,
        IReadOnlyList<PasswordHistoryEntry>? history = null) => new(
            type,
            content.Name!,
            content.Notes,
            login,
            card,
            identity,
            secureNote,
            ssh,
            bankAccount,
            driversLicense,
            passport,
            fields ?? Array.Empty<CipherField>(),
            history ?? Array.Empty<PasswordHistoryEntry>());

    private static LoginDataWire ToWire(CipherLogin login) => new()
    {
        Username = login.Username,
        Password = login.Password,
        PasswordRevisionDate = login.PasswordRevisionDate,
        Uris = login.Uris.Count == 0
            ? null
            : login.Uris.Select(uri => new LoginUriDataWire
            {
                Uri = uri.Uri,
                Match = uri.Match,
            }).ToArray(),
        Totp = login.Totp,
        AutofillOnPageLoad = login.AutofillOnPageLoad,
        Fido2Credentials = login.Fido2Credentials.Count == 0
            ? null
            : login.Fido2Credentials.Select(ToWire).ToArray(),
    };

    private static CipherLogin FromWire(LoginDataWire login) => new(
        login.Username,
        login.Password,
        login.Totp,
        (login.Uris ?? Array.Empty<LoginUriDataWire>())
            .Select(uri => new CipherLoginUri(uri.Uri, uri.Match))
            .ToArray())
    {
        PasswordRevisionDate = login.PasswordRevisionDate,
        AutofillOnPageLoad = login.AutofillOnPageLoad,
        Fido2Credentials = (login.Fido2Credentials ?? Array.Empty<Fido2CredentialDataWire>())
            .Select(FromWire)
            .ToArray(),
    };

    private static Fido2CredentialDataWire ToWire(CipherFido2Credential credential)
    {
        if (credential.Counter is null or < 0)
            throw new InvalidDataException(
                "A blob FIDO2 credential requires a non-negative counter.");
        return new Fido2CredentialDataWire
        {
            CredentialId = NormalizeCredentialIdForWrite(
                Require(credential.CredentialId, "FIDO2 credential ID")),
            KeyType = Require(credential.KeyType, "FIDO2 key type"),
            KeyAlgorithm = Require(credential.KeyAlgorithm, "FIDO2 key algorithm"),
            KeyCurve = Require(credential.KeyCurve, "FIDO2 key curve"),
            KeyValue = Require(credential.KeyValue, "FIDO2 key value"),
            RpId = Require(credential.RpId, "FIDO2 RP ID"),
            UserHandle = credential.UserHandle,
            UserName = credential.UserName,
            Counter = checked((ulong)credential.Counter.Value),
            RpName = credential.RpName,
            UserDisplayName = credential.UserDisplayName,
            Discoverable = credential.Discoverable,
            CreationDate = credential.CreationDate
                ?? throw new InvalidDataException(
                    "A blob FIDO2 credential requires a creation date."),
        };
    }

    private static string NormalizeCredentialIdForWrite(string credentialId) =>
        CredentialId.TryNormalizeStorage(credentialId, out var normalized)
            ? normalized
            : credentialId;

    private static CipherFido2Credential FromWire(Fido2CredentialDataWire credential)
    {
        if (credential.Counter > long.MaxValue)
            throw new InvalidDataException("FIDO2 counter exceeds the supported range.");
        return new CipherFido2Credential(
            Require(credential.CredentialId, "FIDO2 credential ID"),
            Require(credential.KeyType, "FIDO2 key type"),
            Require(credential.KeyAlgorithm, "FIDO2 key algorithm"),
            Require(credential.KeyCurve, "FIDO2 key curve"),
            Require(credential.KeyValue, "FIDO2 key value"),
            Require(credential.RpId, "FIDO2 RP ID"),
            credential.UserHandle,
            credential.UserName,
            (long)credential.Counter,
            credential.RpName,
            credential.UserDisplayName,
            credential.Discoverable,
            credential.CreationDate);
    }

    private static CardDataWire ToWire(CipherCard card) => new()
    {
        CardholderName = card.CardholderName,
        ExpMonth = card.ExpMonth,
        ExpYear = card.ExpYear,
        Code = card.Code,
        Brand = card.Brand,
        Number = card.Number,
    };

    private static IdentityDataWire ToWire(CipherIdentity value) => new()
    {
        Title = value.Title,
        FirstName = value.FirstName,
        MiddleName = value.MiddleName,
        LastName = value.LastName,
        Address1 = value.Address1,
        Address2 = value.Address2,
        Address3 = value.Address3,
        City = value.City,
        State = value.State,
        PostalCode = value.PostalCode,
        Country = value.Country,
        Company = value.Company,
        Email = value.Email,
        Phone = value.Phone,
        Ssn = value.Ssn,
        Username = value.Username,
        PassportNumber = value.PassportNumber,
        LicenseNumber = value.LicenseNumber,
    };

    private static CipherIdentity FromWire(IdentityDataWire value) => new(
        value.Title,
        value.FirstName,
        value.MiddleName,
        value.LastName,
        value.Username,
        value.Company,
        value.Ssn,
        value.PassportNumber,
        value.LicenseNumber,
        value.Email,
        value.Phone,
        value.Address1,
        value.Address2,
        value.Address3,
        value.City,
        value.State,
        value.PostalCode,
        value.Country);

    private static SshKeyDataWire ToWire(CipherSsh value) => new()
    {
        PrivateKey = Require(value.PrivateKey, "SSH private key"),
        PublicKey = Require(value.PublicKey, "SSH public key"),
        Fingerprint = Require(value.Fingerprint, "SSH fingerprint"),
    };

    private static BankAccountDataWire ToWire(CipherBankAccount value) => new()
    {
        BankName = value.BankName,
        NameOnAccount = value.NameOnAccount,
        AccountType = value.AccountType,
        AccountNumber = value.AccountNumber,
        RoutingNumber = value.RoutingNumber,
        BranchNumber = value.BranchNumber,
        Pin = value.Pin,
        SwiftCode = value.SwiftCode,
        Iban = value.Iban,
        BankContactPhone = value.BankContactPhone,
    };

    private static CipherBankAccount FromWire(BankAccountDataWire value) => new(
        value.BankName,
        value.NameOnAccount,
        value.AccountType,
        value.AccountNumber,
        value.RoutingNumber,
        value.BranchNumber,
        value.Pin,
        value.SwiftCode,
        value.Iban,
        value.BankContactPhone);

    private static DriversLicenseDataWire ToWire(CipherDriversLicense value) => new()
    {
        FirstName = value.FirstName,
        MiddleName = value.MiddleName,
        LastName = value.LastName,
        DateOfBirth = value.DateOfBirth,
        LicenseNumber = value.LicenseNumber,
        IssuingCountry = value.IssuingCountry,
        IssuingState = value.IssuingState,
        IssueDate = value.IssueDate,
        ExpirationDate = value.ExpirationDate,
        IssuingAuthority = value.IssuingAuthority,
        LicenseClass = value.LicenseClass,
    };

    private static CipherDriversLicense FromWire(DriversLicenseDataWire value) => new(
        value.FirstName,
        value.MiddleName,
        value.LastName,
        value.DateOfBirth,
        value.LicenseNumber,
        value.IssuingCountry,
        value.IssuingState,
        value.IssueDate,
        value.ExpirationDate,
        value.IssuingAuthority,
        value.LicenseClass);

    private static PassportDataWire ToWire(CipherPassport value) => new()
    {
        Surname = value.Surname,
        GivenName = value.GivenName,
        DateOfBirth = value.DateOfBirth,
        Sex = value.Sex,
        BirthPlace = value.BirthPlace,
        Nationality = value.Nationality,
        IssuingCountry = value.IssuingCountry,
        PassportNumber = value.PassportNumber,
        PassportType = value.PassportType,
        NationalIdentificationNumber = value.NationalIdentificationNumber,
        IssuingAuthority = value.IssuingAuthority,
        IssueDate = value.IssueDate,
        ExpirationDate = value.ExpirationDate,
    };

    private static CipherPassport FromWire(PassportDataWire value) => new(
        value.Surname,
        value.GivenName,
        value.DateOfBirth,
        value.Sex,
        value.BirthPlace,
        value.Nationality,
        value.IssuingCountry,
        value.PassportNumber,
        value.PassportType,
        value.NationalIdentificationNumber,
        value.IssuingAuthority,
        value.IssueDate,
        value.ExpirationDate);

    private static Exception MissingTypeData(CipherType type) =>
        new InvalidDataException($"Cipher type {(int)type} is missing its blob payload.");

    private static string Require(string? value, string name) =>
        value ?? throw new InvalidDataException(
            $"{name} is required by cipher blob version 1.");

    private static void WriteJsonValue(CborWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var properties = value.EnumerateObject().ToArray();
                writer.WriteStartMap(properties.Length);
                foreach (var property in properties)
                {
                    writer.WriteTextString(property.Name);
                    WriteJsonValue(writer, property.Value);
                }
                writer.WriteEndMap();
                break;
            }
            case JsonValueKind.Array:
            {
                var items = value.EnumerateArray().ToArray();
                writer.WriteStartArray(items.Length);
                foreach (var item in items)
                    WriteJsonValue(writer, item);
                writer.WriteEndArray();
                break;
            }
            case JsonValueKind.String:
                writer.WriteTextString(value.GetString()!);
                break;
            case JsonValueKind.Number when value.TryGetInt64(out var signed):
                writer.WriteInt64(signed);
                break;
            case JsonValueKind.Number when value.TryGetUInt64(out var unsigned):
                writer.WriteUInt64(unsigned);
                break;
            case JsonValueKind.True:
                writer.WriteBoolean(true);
                break;
            case JsonValueKind.False:
                writer.WriteBoolean(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNull();
                break;
            default:
                throw new InvalidDataException(
                    "Cipher blob contains an unsupported JSON value.");
        }
    }

    private static void ReadCborValue(
        CborReader reader,
        Utf8JsonWriter writer,
        int depth)
    {
        if (depth > 64)
            throw new InvalidDataException("Cipher blob CBOR nesting exceeds the limit.");

        switch (reader.PeekState())
        {
            case CborReaderState.StartMap:
            {
                var length = reader.ReadStartMap()
                    ?? throw new InvalidDataException(
                        "Indefinite CBOR maps are not supported.");
                writer.WriteStartObject();
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; index < length; index++)
                {
                    if (reader.PeekState() != CborReaderState.TextString)
                        throw new InvalidDataException(
                            "Cipher blob CBOR map keys must be text.");
                    var name = reader.ReadTextString();
                    if (!names.Add(name))
                        throw new InvalidDataException(
                            "Cipher blob CBOR contains a duplicate key.");
                    writer.WritePropertyName(name);
                    ReadCborValue(reader, writer, depth + 1);
                }
                reader.ReadEndMap();
                writer.WriteEndObject();
                break;
            }
            case CborReaderState.StartArray:
            {
                var length = reader.ReadStartArray()
                    ?? throw new InvalidDataException(
                        "Indefinite CBOR arrays are not supported.");
                writer.WriteStartArray();
                for (var index = 0; index < length; index++)
                    ReadCborValue(reader, writer, depth + 1);
                reader.ReadEndArray();
                writer.WriteEndArray();
                break;
            }
            case CborReaderState.TextString:
                writer.WriteStringValue(reader.ReadTextString());
                break;
            case CborReaderState.UnsignedInteger:
                writer.WriteNumberValue(reader.ReadUInt64());
                break;
            case CborReaderState.NegativeInteger:
                writer.WriteNumberValue(reader.ReadInt64());
                break;
            case CborReaderState.Boolean:
                writer.WriteBooleanValue(reader.ReadBoolean());
                break;
            case CborReaderState.Null:
                reader.ReadNull();
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException(
                    $"Cipher blob contains unsupported CBOR state {reader.PeekState()}.");
        }
    }
}
