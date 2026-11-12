using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Crypto.V2;
using Konscious.Security.Cryptography;

namespace Vault.Porting;

public sealed record EncryptedBackupKdfOptions(
    KdfType KdfType = KdfType.Pbkdf2,
    int Iterations = 600_000,
    int? MemoryMiB = null,
    int? Parallelism = null);

/// <summary>
/// Implements the password-protected and account-restricted Bitwarden JSON formats.
/// Contract reference: bitwarden/clients at 752719b07c6326f5cf8ea297573f2094f60d13e3,
/// libs/tools/export-vault-core/src/services/{base,individual}-vault-export.service.ts,
/// libs/legacy-crypto/src/services/default-key-generation.service.ts, and the matching importers.
/// A password export encrypts the complete unencrypted JSON document; an account export
/// retains the individual encrypted cipher/folder fields and an account-key validation value.
/// </summary>
public static class EncryptedBitwardenJsonCodec
{
    private const string ValidationProperty = "encKeyValidation_DO_NOT_EDIT";
    private const int MinimumImportedPbkdf2Iterations = 5_000;
    private const int MaximumKdfIterations = 2_000_000;
    private const int MaximumPasswordCharacters = 1_024;
    private const int MaximumSaltCharacters = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ImportProtection ReadProtection(string json)
    {
        using var document = ParseDocument(json);
        return ReadProtection(document.RootElement);
    }

    public static string EncryptWithPassword(
        string plaintextJson,
        string password,
        EncryptedBackupKdfOptions? options = null)
    {
        ValidatePassword(password, forExport: true);
        ValidatePlaintextDocument(plaintextJson);
        options ??= new EncryptedBackupKdfOptions();
        ValidateKdfOptions(options, forExport: true);

        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var salt = Convert.ToBase64String(saltBytes);
        CryptographicOperations.ZeroMemory(saltBytes);
        using var exportKey = DeriveExportKey(password, salt, options);
        var crypto = new CryptoService();
        var validationBytes = StrictUtf8.GetBytes(Guid.NewGuid().ToString("D"));
        var plaintextBytes = StrictUtf8.GetBytes(plaintextJson);
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("encrypted", true);
                writer.WriteBoolean("passwordProtected", true);
                writer.WriteString("salt", salt);
                writer.WriteNumber("kdfType", (int)options.KdfType);
                writer.WriteNumber("kdfIterations", options.Iterations);
                if (options.KdfType == KdfType.Argon2id)
                {
                    writer.WriteNumber("kdfMemory", options.MemoryMiB!.Value);
                    writer.WriteNumber("kdfParallelism", options.Parallelism!.Value);
                }
                writer.WriteString(ValidationProperty, crypto.Encrypt(validationBytes, exportKey).ToString());
                writer.WriteString("data", crypto.Encrypt(plaintextBytes, exportKey).ToString());
                writer.WriteEndObject();
            }
            return ReadBoundedOutput(buffer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(validationBytes);
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public static string DecryptWithPassword(string encryptedJson, string password)
    {
        ValidatePassword(password, forExport: false);
        using var document = ParseDocument(encryptedJson);
        var root = document.RootElement;
        if (ReadProtection(root) != ImportProtection.Password)
            throw new InvalidDataException("The selected backup is not password protected.");

        var options = ReadKdfOptions(root);
        ValidateKdfOptions(options, forExport: false);
        var salt = RequiredString(root, "salt", MaximumSaltCharacters);
        if (string.IsNullOrWhiteSpace(salt))
            throw new InvalidDataException("The backup salt is missing.");

        // The salt's base64 text itself is the KDF salt. Decoding it before KDF is incompatible.
        using var exportKey = DeriveExportKey(password, salt, options);
        var crypto = new CryptoService();
        ValidateMarker(DecryptAuthenticated(RequiredString(root, ValidationProperty, 1_024), exportKey, crypto));
        var plaintext = DecryptAuthenticated(RequiredString(root, "data"), exportKey, crypto);
        try
        {
            if ((ulong)plaintext.Length > VaultPortingLimits.MaximumFileSizeBytes)
                throw new InvalidDataException("The decrypted backup exceeds the supported size limit.");
            var result = StrictUtf8.GetString(plaintext);
            ValidatePlaintextDocument(result);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static string ExportAccountRestricted(
        IReadOnlyList<Cipher> ciphers,
        IReadOnlyList<Folder> folders,
        VaultUserKey userKey,
        VaultCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(ciphers);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(userKey);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (userKey.Legacy is { MacKey: not { Length: 32 } })
            throw new NotSupportedException("Account-restricted backup requires an authenticated account key. Use a password-protected backup for a legacy key.");
        var crypto = new CryptoService();
        var encryptor = new CipherEncryptor(crypto);
        // This is local serialization of already-readable content, not a server mutation.
        // Actual import applies the destination server's capability and organization policies.
        var localSchema = new VaultCapabilities(true, true, true);
        var validationBytes = StrictUtf8.GetBytes(Guid.NewGuid().ToString("D"));
        try
        {
            var validation = userKey.Legacy is { } legacy
                ? crypto.Encrypt(validationBytes, legacy).ToString()
                : userKey.V2!.Encrypt(validationBytes, CoseContentFormat.Utf8);

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("encrypted", true);
                writer.WriteString(ValidationProperty, validation);
                writer.WriteStartArray("folders");
                foreach (var folder in folders)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", folder.Id);
                    writer.WriteString("name", userKey.Legacy is { } folderKey
                        ? encryptor.EncryptFolderName(folder.Name, folderKey)
                        : encryptor.EncryptFolderName(folder.Name, userKey.V2!));
                    if (folder.RevisionDate != default)
                        writer.WriteString("revisionDate", folder.RevisionDate);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("items");
                foreach (var cipher in ciphers)
                {
                    if (cipher.IsDeleted || !string.IsNullOrWhiteSpace(cipher.OrganizationId))
                        throw new InvalidDataException("A personal backup cannot contain deleted or organization items.");

                    // A backup is an independent encrypted copy. Reusing the source item key is
                    // unnecessary because attachments are excluded. Fresh item keys also allow
                    // a readable V1 blob item to be serialized without changing its live format.
                    var exportCipher = ImportCipherNormalizer.AsNewPersonalCipher(cipher)
                        .WithFolderId(cipher.FolderId);
                    var request = userKey.Legacy is { } cipherKey
                        ? encryptor.Encrypt(exportCipher, cipherKey, localSchema)
                        : encryptor.Encrypt(exportCipher, userKey.V2!, localSchema);
                    var encoded = JsonSerializer.SerializeToElement(request, ApiJsonContext.Default.CipherRequest);
                    writer.WriteStartObject();
                    writer.WriteString("id", cipher.Id);
                    if (cipher.CreationDate != default)
                        writer.WriteString("creationDate", cipher.CreationDate);
                    if (cipher.RevisionDate != default)
                        writer.WriteString("revisionDate", cipher.RevisionDate);
                    foreach (var property in encoded.EnumerateObject())
                    {
                        // Context markers are transport metadata, not part of CipherExport.
                        if (property.Name is "encryptedFor" or "encryptedByKeyId" or "lastKnownRevisionDate")
                            continue;
                        property.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return ReadBoundedOutput(buffer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(validationBytes);
        }
    }

    public static string DecryptAccountRestricted(string encryptedJson, VaultUserKey userKey)
    {
        ArgumentNullException.ThrowIfNull(userKey);
        using var document = ParseDocument(encryptedJson);
        var root = document.RootElement;
        if (ReadProtection(root) != ImportProtection.Account)
            throw new InvalidDataException("The selected backup is not account restricted.");
        if (root.TryGetProperty("collections", out _))
            throw new InvalidDataException("Organization backups cannot be imported into the personal vault.");

        var crypto = new CryptoService();
        var validationText = RequiredString(root, ValidationProperty, 1_024);
        var validation = userKey.Legacy is { } legacy
            ? DecryptAuthenticated(validationText, legacy, crypto)
            : userKey.V2!.Decrypt(validationText, CoseContentFormat.Utf8);
        ValidateMarker(validation);

        var foldersJson = RequiredArray(root, "folders");
        var itemsJson = RequiredArray(root, "items");
        var decryptor = new VaultDecryptor(crypto);
        var folders = new List<Folder>(foldersJson.GetArrayLength());
        foreach (var encodedFolder in foldersJson.EnumerateArray())
        {
            RequireAuthenticatedValue(encodedFolder, "name", userKey.IsV2, required: true);
            var dto = encodedFolder.Deserialize(ApiJsonContext.Default.FolderDto)
                ?? throw new InvalidDataException("The backup contains an invalid folder.");
            folders.Add(userKey.Legacy is { } folderKey
                ? decryptor.DecryptFolder(dto, folderKey)
                : decryptor.DecryptFolder(dto, userKey.V2!));
        }

        var ciphers = new List<Cipher>(itemsJson.GetArrayLength());
        foreach (var encodedCipher in itemsJson.EnumerateArray())
        {
            ValidateAccountCipherAuthentication(encodedCipher, userKey.IsV2);
            var dto = encodedCipher.Deserialize(ApiJsonContext.Default.CipherDto)
                ?? throw new InvalidDataException("The backup contains an invalid item.");
            if (!string.IsNullOrWhiteSpace(dto.OrganizationId) || dto.DeletedDate is not null)
                throw new InvalidDataException("The personal backup contains a deleted or organization item.");
            ciphers.Add(userKey.Legacy is { } cipherKey
                ? decryptor.DecryptCipher(dto, cipherKey)
                : decryptor.DecryptCipher(dto, userKey.V2!));
        }

        // No partial result is returned: any field authentication or parsing error rejects the backup.
        var plaintext = BitwardenJsonCodec.Serialize(ciphers, folders);
        ValidatePlaintextDocument(plaintext);
        return plaintext;
    }

    private static void ValidateAccountCipherAuthentication(JsonElement cipher, bool v2)
    {
        RequireAuthenticatedValue(cipher, "key", v2, required: v2);
        RequireAuthenticatedValue(cipher, "name", type7: false);
        RequireAuthenticatedValue(cipher, "notes", type7: false);
        foreach (var property in new[]
        {
            "login", "card", "identity", "sshKey", "bankAccount", "driversLicense", "passport", "fields", "passwordHistory",
        })
        {
            if (TryGetCipherProperty(cipher, property, out var value))
                ValidateEncryptedChildren(value);
        }

        if (TryGetCipherProperty(cipher, "data", out var data) && data.ValueKind != JsonValueKind.Null)
        {
            if (data.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("An encrypted backup contains an invalid cipher data container.");
            var opaque = data.GetString();
            if (!new CipherBlobCodec(new CryptoService()).IsBlob(opaque))
                throw new CryptographicException("An encrypted backup cannot downgrade its authenticated cipher data container.");
            // The production blob decoder requires a type 2 wrapped CEK and authenticates
            // the COSE AEAD envelope before decoding any plaintext fields.
        }
        else if (v2)
        {
            throw new CryptographicException("A V2 encrypted backup requires an authenticated cipher data container.");
        }
    }

    private static void ValidateEncryptedChildren(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    // These dates are public export metadata rather than EncString fields.
                    if (property.Name is not ("creationDate" or "passwordRevisionDate" or "lastUsedDate"))
                        RequireAuthenticatedCiphertext(property.Value.GetString()!, type7: false);
                }
                else
                {
                    ValidateEncryptedChildren(property.Value);
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
                ValidateEncryptedChildren(child);
        }
    }

    private static void RequireAuthenticatedValue(JsonElement parent, string name, bool type7, bool required = false)
    {
        if (!TryGetCipherProperty(parent, name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required)
                throw new InvalidDataException("An encrypted backup is missing a required authenticated field.");
            return;
        }
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("An encrypted backup contains a mistyped encrypted field.");
        RequireAuthenticatedCiphertext(value.GetString()!, type7);
    }

    private static bool TryGetCipherProperty(JsonElement parent, string name, out JsonElement value)
    {
        // The API DTO reader accepts case-insensitive names. Authentication checks must
        // inspect the exact same values rather than allowing casing to bypass a field gate.
        if (parent.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("An encrypted backup contains a non-object record.");
        foreach (var property in parent.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static void RequireAuthenticatedCiphertext(string value, bool type7)
    {
        if (type7)
        {
            if (!value.StartsWith("7.", StringComparison.Ordinal))
                throw new CryptographicException("A V2 encrypted backup requires authenticated type 7 ciphertext.");
            return;
        }
        var parsed = EncString.Parse(value);
        if (parsed.Type != EncryptionType.AesCbc256_HmacSha256_B64
            || parsed.Iv.Length != 16 || parsed.Mac is not { Length: 32 }
            || parsed.Ct.Length == 0 || parsed.Ct.Length % 16 != 0)
        {
            throw new CryptographicException("Unauthenticated legacy account backups are not accepted. Export a password-protected backup from the original client.");
        }
    }

    private static ImportProtection ReadProtection(JsonElement root)
    {
        if (!root.TryGetProperty("encrypted", out var encrypted))
            return ImportProtection.None;
        if (encrypted.ValueKind == JsonValueKind.False)
            return ImportProtection.None;
        if (encrypted.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("The backup encryption flag must be a boolean.");
        if (!root.TryGetProperty("passwordProtected", out var passwordProtected))
            return ImportProtection.Account;
        return passwordProtected.ValueKind switch
        {
            JsonValueKind.True => ImportProtection.Password,
            JsonValueKind.False => ImportProtection.Account,
            _ => throw new InvalidDataException("The password protection flag must be a boolean."),
        };
    }

    private static JsonDocument ParseDocument(string json)
    {
        VaultPortingLimits.ValidateContent(json);
        if ((ulong)StrictUtf8.GetByteCount(json) > VaultPortingLimits.MaximumFileSizeBytes)
            throw new InvalidDataException("The vault backup exceeds the supported byte limit.");
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("A vault backup must be a JSON object.");
            RejectDuplicateProperties(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("The vault backup contains duplicate JSON properties.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }

    private static void ValidatePlaintextDocument(string plaintextJson)
    {
        using var document = ParseDocument(plaintextJson);
        if (ReadProtection(document.RootElement) != ImportProtection.None)
            throw new InvalidDataException("The decrypted payload must be an unencrypted Bitwarden JSON backup.");
        RequiredArray(document.RootElement, "folders");
        RequiredArray(document.RootElement, "items");
    }

    private static EncryptedBackupKdfOptions ReadKdfOptions(JsonElement root) => new(
        (KdfType)RequiredInteger(root, "kdfType"),
        RequiredInteger(root, "kdfIterations"),
        OptionalInteger(root, "kdfMemory"),
        OptionalInteger(root, "kdfParallelism"));

    private static void ValidateKdfOptions(EncryptedBackupKdfOptions options, bool forExport)
    {
        switch (options.KdfType)
        {
            case KdfType.Pbkdf2:
                var minimum = forExport
                    ? KdfParameterPolicy.MinimumPbkdf2WriteIterations
                    : MinimumImportedPbkdf2Iterations;
                if (options.Iterations < minimum || options.Iterations > MaximumKdfIterations)
                    throw new InvalidDataException("The backup PBKDF2 iteration count is outside the supported range.");
                break;
            case KdfType.Argon2id:
                if (options.Iterations is < 2 or > 10
                    || options.MemoryMiB is not (>= 16 and <= 1_024)
                    || options.Parallelism is not (>= 1 and <= 16))
                    throw new InvalidDataException("The backup Argon2id parameters are outside the supported range.");
                break;
            default:
                throw new InvalidDataException("The backup uses an unsupported key derivation algorithm.");
        }
    }

    private static SymmetricCryptoKey DeriveExportKey(
        string password,
        string salt,
        EncryptedBackupKdfOptions options)
    {
        var passwordBytes = StrictUtf8.GetBytes(password);
        var saltBytes = StrictUtf8.GetBytes(salt);
        byte[]? material = null;
        byte[]? saltHash = null;
        try
        {
            if (options.KdfType == KdfType.Pbkdf2)
            {
                material = Rfc2898DeriveBytes.Pbkdf2(
                    passwordBytes, saltBytes, options.Iterations, HashAlgorithmName.SHA256, 32);
            }
            else
            {
                saltHash = SHA256.HashData(saltBytes);
                using var argon2 = new Argon2id(passwordBytes)
                {
                    Salt = saltHash,
                    Iterations = options.Iterations,
                    MemorySize = checked(options.MemoryMiB!.Value * 1_024),
                    DegreeOfParallelism = options.Parallelism!.Value,
                };
                material = argon2.GetBytes(32);
            }
            return new CryptoService().StretchMasterKey(material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(saltBytes);
            if (material is not null)
                CryptographicOperations.ZeroMemory(material);
            if (saltHash is not null)
                CryptographicOperations.ZeroMemory(saltHash);
        }
    }

    private static byte[] DecryptAuthenticated(string encoded, SymmetricCryptoKey key, CryptoService crypto)
    {
        var cipher = EncString.Parse(encoded);
        if (cipher.Type != EncryptionType.AesCbc256_HmacSha256_B64
            || cipher.Iv.Length != 16 || cipher.Mac is not { Length: 32 }
            || cipher.Ct.Length == 0 || cipher.Ct.Length % 16 != 0)
        {
            throw new CryptographicException("An encrypted backup requires authenticated AES-256-CBC ciphertext.");
        }
        return crypto.Decrypt(cipher, key);
    }

    private static void ValidateMarker(byte[] marker)
    {
        try
        {
            if (marker.Length != 36 || !Guid.TryParseExact(StrictUtf8.GetString(marker), "D", out _))
                throw new CryptographicException("The encrypted backup key validation value is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(marker);
        }
    }

    private static void ValidatePassword(string password, bool forExport)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (string.IsNullOrWhiteSpace(password) || password.Length > MaximumPasswordCharacters
            || forExport && password.Length < VaultPortingLimits.MinimumExportPasswordLength)
            throw new ArgumentException("The backup password does not meet the supported length requirements.", nameof(password));
    }

    private static string RequiredString(JsonElement root, string name, int maximumLength = VaultPortingLimits.MaximumContentCharacters)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"The backup field '{name}' must be a string.");
        var text = value.GetString()!;
        if (text.Length == 0 || text.Length > maximumLength)
            throw new InvalidDataException($"The backup field '{name}' has an invalid length.");
        return text;
    }

    private static JsonElement RequiredArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"The backup field '{name}' must be an array.");
        return value;
    }

    private static int RequiredInteger(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number))
            throw new InvalidDataException($"The backup field '{name}' must be an integer.");
        return number;
    }

    private static int? OptionalInteger(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new InvalidDataException($"The backup field '{name}' must be an integer.");
        return number;
    }

    private static string ReadBoundedOutput(MemoryStream buffer)
    {
        if ((ulong)buffer.Length > VaultPortingLimits.MaximumFileSizeBytes)
            throw new InvalidDataException("The encrypted backup exceeds the supported byte limit.");
        var output = StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        VaultPortingLimits.ValidateContent(output);
        return output;
    }
}
