using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Enums;
using Core.Models;
using Core.Services;

namespace Vault.Porting;

/// <summary>
/// Reads version 3 of 1Password's unencrypted ZIP export without extracting files.
/// Source contract: https://support.1password.com/1pux-format/ and Bitwarden clients
/// commit 78d082ed393d608410ae6102bd81c7680d9cc8a8, onepassword-1pux-importer-types.ts.
/// Unsupported binary content, unknown data shapes and ambiguous native fields fail the
/// entire parse. No partially parsed items are returned to an importing caller.
/// </summary>
public static class OnePasswordArchiveImportCodec
{
    public const int MaximumArchiveBytes = (int)VaultPortingLimits.MaximumFileSizeBytes;
    public const int MaximumDataBytes = 32 * 1024 * 1024;
    public const int MaximumArchiveEntries = 4_096;
    public const int MaximumItems = 50_000;
    public const int MaximumFolders = 1_024;
    public const int MaximumFieldsPerItem = 2_048;
    private const int MaximumStringCharacters = 1_048_576;
    private const int MaximumHistoryEntries = 4_096;
    private const int MaximumJsonNodes = 500_000;
    private const int MaximumAttributesBytes = 65_536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly uint[] CrcTable = CreateCrcTable();
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 64 };

    public static PortingData Parse(ReadOnlyMemory<byte> archiveBytes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (archiveBytes.Length > MaximumArchiveBytes)
            throw Invalid("The 1PUX archive exceeds the supported size limit.");
        var owned = archiveBytes.ToArray();
        try
        {
            using var memory = new MemoryStream(owned, writable: false);
            return ParseOwned(memory, owned, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(owned);
        }
    }

    /// <summary>Reads from the current stream position and leaves the caller's stream open.</summary>
    public static PortingData Parse(Stream archiveStream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        if (!archiveStream.CanRead)
            throw new ArgumentException("The archive stream must be readable.", nameof(archiveStream));
        using var memory = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = archiveStream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (read > MaximumArchiveBytes - memory.Length)
                    throw Invalid("The 1PUX archive exceeds the supported size limit.");
                memory.Write(buffer, 0, read);
            }
            memory.Position = 0;
            return ParseOwned(memory, memory.GetBuffer().AsMemory(0, checked((int)memory.Length)), ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
            CryptographicOperations.ZeroMemory(memory.GetBuffer().AsSpan(0, checked((int)memory.Length)));
        }
    }

    private static PortingData ParseOwned(MemoryStream memory, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var headers = ReadCentralDirectory(bytes.Span);
        if (!headers.Any(header => header.Name == "export.attributes")
            || !headers.Any(header => header.Name == "export.data"))
            throw Invalid("A 1PUX archive must contain export.attributes and export.data at its root.");

        foreach (var header in headers)
        {
            if (header.Name is "export.attributes" or "export.data") continue;
            if (header.Directory && header.Name.StartsWith("files/", StringComparison.Ordinal)) continue;
            if (header.Name.StartsWith("files/", StringComparison.Ordinal))
                throw Invalid("This 1PUX archive contains attachments or custom icons. Their contents cannot be imported without losing data.");
            throw Invalid("The 1PUX archive contains an unsupported entry.");
        }

        byte[]? attributes = null;
        byte[]? data = null;
        try
        {
            using var zip = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: StrictUtf8);
            if (zip.Entries.Count != headers.Count)
                throw Invalid("The ZIP entry count is inconsistent.");
            for (var index = 0; index < headers.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var header = headers[index];
                var entry = zip.Entries[index];
                if (entry.FullName != header.Name || entry.Length != header.Length || entry.CompressedLength != header.CompressedLength)
                    throw Invalid("The ZIP directory is inconsistent.");
                if (header.Name == "export.attributes")
                    attributes = ReadEntry(entry, header, MaximumAttributesBytes, ct);
                else if (header.Name == "export.data")
                    data = ReadEntry(entry, header, MaximumDataBytes, ct);
                else if (header.Directory)
                    ValidateDirectoryPayload(bytes, header, ct);
            }
            _ = StrictUtf8.GetCharCount(WithoutBom(attributes!).Span);
            _ = StrictUtf8.GetCharCount(WithoutBom(data!).Span);
            using var attributesDocument = JsonDocument.Parse(WithoutBom(attributes!), JsonOptions);
            ValidateJson(attributesDocument.RootElement, ct);
            var attributeRoot = Object(attributesDocument.RootElement);
            CheckProperties(attributeRoot, "version", "description", "createdAt");
            if (!attributeRoot.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number != 3)
                throw Invalid("Only version 3 of the 1PUX export format is supported.");
            _ = OptionalString(attributeRoot, "description");
            _ = OptionalUnixTime(attributeRoot, "createdAt");

            using var dataDocument = JsonDocument.Parse(WithoutBom(data!), JsonOptions);
            ValidateJson(dataDocument.RootElement, ct);
            return ParseData(dataDocument.RootElement, ct);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or NotSupportedException
            or CryptographicException or InvalidOperationException)
        {
            throw Invalid("The 1PUX archive contains malformed or unsupported data.", exception);
        }
        finally
        {
            if (attributes is not null) CryptographicOperations.ZeroMemory(attributes);
            if (data is not null) CryptographicOperations.ZeroMemory(data);
        }
    }

    private static PortingData ParseData(JsonElement root, CancellationToken ct)
    {
        Object(root);
        CheckProperties(root, "accounts");
        if (!root.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
            throw Invalid("The 1PUX export must contain an accounts array.");
        var ciphers = new List<Cipher>();
        var folders = new List<Folder>();
        var relations = new List<(int CipherIndex, int FolderIndex)>();
        var accountIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in accounts.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            Object(account);
            CheckProperties(account, "attrs", "vaults");
            var accountAttributes = RequiredObject(account, "attrs");
            CheckProperties(accountAttributes, "accountName", "name", "avatar", "email", "uuid", "domain");
            var accountId = Identifier(accountAttributes, "uuid");
            if (!accountIds.Add(accountId)) throw Invalid("The export contains a duplicate account identifier.");
            var accountName = OptionalString(accountAttributes, "accountName") ?? OptionalString(accountAttributes, "name") ?? string.Empty;
            var vaultIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var vault in OptionalArray(account, "vaults"))
            {
                ct.ThrowIfCancellationRequested();
                if (folders.Count >= MaximumFolders) throw Invalid("The 1PUX export contains too many vaults.");
                Object(vault);
                CheckProperties(vault, "attrs", "items");
                var vaultAttributes = RequiredObject(vault, "attrs");
                CheckProperties(vaultAttributes, "uuid", "desc", "avatar", "name", "type");
                if (!vaultIds.Add(Identifier(vaultAttributes, "uuid")))
                    throw Invalid("The export contains a duplicate vault identifier.");
                var vaultName = OptionalString(vaultAttributes, "name") ?? string.Empty;
                var folderIndex = folders.Count;
                var folderId = folderIndex.ToString(CultureInfo.InvariantCulture);
                var folderName = accounts.GetArrayLength() > 1 && accountName.Length > 0
                    ? accountName + " / " + vaultName : vaultName;
                folders.Add(new Folder { Id = folderId, Name = folderName });
                var itemIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in OptionalArray(vault, "items"))
                {
                    ct.ThrowIfCancellationRequested();
                    if (ciphers.Count >= MaximumItems) throw Invalid("The 1PUX export contains too many items.");
                    Object(item);
                    if (!itemIds.Add(Identifier(item, "uuid")))
                        throw Invalid("The export contains duplicate item identifiers in one vault.");
                    ciphers.Add(ParseItem(item, folderId, accountAttributes, vaultAttributes, ct));
                    relations.Add((ciphers.Count - 1, folderIndex));
                }
            }
        }
        return new PortingData(ciphers, folders, relations);
    }

    private static Cipher ParseItem(JsonElement item, string folderId, JsonElement account, JsonElement vault, CancellationToken ct)
    {
        CheckProperties(item, "uuid", "favIndex", "createdAt", "updatedAt", "state", "categoryUuid", "details", "overview", "file");
        if (item.TryGetProperty("file", out var file) && HasContent(file))
            throw Invalid("This 1PUX export contains document or attachment content that cannot be imported without losing data.");
        var state = OptionalString(item, "state") ?? "active";
        if (state != "active")
            throw Invalid("Archived or deleted 1Password items require a separate reviewed conversion; no archive timestamp is supplied by 1PUX.");
        var category = RequiredString(item, "categoryUuid");
        var type = category switch
        {
            "001" or "005" => CipherType.Login,
            "002" => CipherType.Card,
            "003" => CipherType.SecureNote,
            "004" => CipherType.Identity,
            "101" => CipherType.BankAccount,
            "103" => CipherType.DriversLicense,
            "106" => CipherType.Passport,
            "114" => CipherType.SshKey,
            "006" => throw Invalid("1Password Document items require binary attachment import and cannot be converted without losing data."),
            _ => throw Invalid("The export contains a 1Password category that has no lossless native mapping. Export those items separately for review."),
        };
        var details = RequiredObject(item, "details");
        var overview = RequiredObject(item, "overview");
        CheckProperties(details, "loginFields", "notesPlain", "sections", "passwordHistory", "documentAttributes", "password");
        CheckProperties(overview, "subtitle", "title", "url", "urls", "tags", "icons", "ps", "pbe", "pgrng", "watchtowerExclusions");
        if (details.TryGetProperty("documentAttributes", out var document) && HasContent(document))
            throw Invalid("This 1PUX export contains document attributes. Binary content must be imported separately.");
        if (overview.TryGetProperty("icons", out var icons) && HasContent(icons))
            throw Invalid("This 1PUX export contains a custom icon that cannot be preserved by this import.");
        if (overview.TryGetProperty("watchtowerExclusions", out var exclusions) && HasContent(exclusions))
            throw Invalid("This 1PUX export contains Watchtower exclusions that require manual review.");
        var fields = new List<CipherField>();
        Add(fields, "1Password category", category, false);
        Add(fields, "1Password item UUID", RequiredString(item, "uuid"), false);
        Add(fields, "1Password account", OptionalString(account, "accountName"), false);
        Add(fields, "1Password account name", OptionalString(account, "name"), false);
        Add(fields, "1Password account email", OptionalString(account, "email"), false);
        Add(fields, "1Password account domain", OptionalString(account, "domain"), false);
        Add(fields, "1Password vault", OptionalString(vault, "name"), false);
        Add(fields, "1Password vault description", OptionalString(vault, "desc"), false);
        var created = OptionalUnixTime(item, "createdAt");
        var updated = OptionalUnixTime(item, "updatedAt");
        Add(fields, "1Password created", created?.ToString("O", CultureInfo.InvariantCulture), false);
        Add(fields, "1Password modified", updated?.ToString("O", CultureInfo.InvariantCulture), false);

        string? username = null;
        var standalonePassword = OptionalString(details, "password");
        string? password = standalonePassword;
        var designationSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var loginField in OptionalArray(details, "loginFields"))
        {
            if (loginField.ValueKind == JsonValueKind.Null) continue;
            Object(loginField);
            CheckProperties(loginField, "value", "name", "id", "fieldType", "type", "designation");
            var value = OptionalString(loginField, "value") ?? string.Empty;
            var designation = OptionalString(loginField, "designation");
            var fieldType = OptionalString(loginField, "fieldType") ?? OptionalString(loginField, "type");
            if (designation is "username" or "password")
            {
                if (!designationSeen.Add(designation)) throw Invalid("A 1PUX item has ambiguous login designations.");
                if (designation == "username") username = value;
                else if (category != "005" || password is null) password = value;
            }
            var label = OptionalString(loginField, "name") ?? OptionalString(loginField, "id");
            if (string.IsNullOrEmpty(label)) label = "Web form field";
            Add(fields, "1Password web form / " + label, value, fieldType == "P" || designation == "password", includeEmpty: true);
        }
        if (standalonePassword is not null)
            Add(fields, "1Password item password", standalonePassword, true, includeEmpty: true);

        var parsed = new List<SourceField>();
        var reprompt = false;
        foreach (var section in OptionalArray(details, "sections"))
        {
            ct.ThrowIfCancellationRequested();
            if (section.ValueKind == JsonValueKind.Null) continue;
            Object(section);
            CheckProperties(section, "title", "name", "fields", "hideAddAnotherField");
            var sectionTitle = OptionalString(section, "title") ?? string.Empty;
            foreach (var field in OptionalArray(section, "fields"))
            {
                if (field.ValueKind == JsonValueKind.Null) continue;
                Object(field);
                CheckProperties(field, "title", "id", "value", "guarded", "multiline", "dontGenerate", "inputTraits", "placeholder", "clipboardFilter", "indexAtSource");
                var title = OptionalString(field, "title") ?? OptionalString(field, "id") ?? "Field";
                var id = OptionalString(field, "id") ?? string.Empty;
                var guarded = OptionalBoolean(field, "guarded");
                reprompt |= guarded;
                var label = sectionTitle.Length == 0 ? title : sectionTitle + " / " + title;
                if (field.TryGetProperty("inputTraits", out var traits) && traits.ValueKind != JsonValueKind.Null)
                {
                    Object(traits);
                    CheckProperties(traits, "keyboard", "correction", "capitalization");
                    _ = OptionalString(traits, "keyboard");
                    _ = OptionalString(traits, "correction");
                    _ = OptionalString(traits, "capitalization");
                }
                var value = field.TryGetProperty("value", out var sourceValue) ? sourceValue : default;
                var converted = ReadField(id, label, value, guarded, fields);
                if (converted is not null) parsed.Add(converted);
                Add(fields, label + " / clipboard filter", OptionalString(field, "clipboardFilter"), guarded);
                Add(fields, label + " / placeholder", OptionalString(field, "placeholder"), guarded);
            }
        }
        var uris = ReadUris(overview, fields);
        if (type == CipherType.Login)
        {
            if (string.IsNullOrEmpty(username)) username = NativeLoginText(parsed, "username", "string", "email");
            if (string.IsNullOrEmpty(password)
                && !(category == "005" && standalonePassword is not null))
                password = NativeLoginText(parsed, "password", "string", "concealed");
        }
        var history = ReadHistory(details);
        var tagIndex = 0;
        foreach (var tag in OptionalArray(overview, "tags"))
        {
            if (tag.ValueKind != JsonValueKind.String) throw Invalid("A 1Password tag must be text.");
            Add(fields, "1Password tag " + (++tagIndex).ToString(CultureInfo.InvariantCulture), String(tag), false, includeEmpty: true);
        }
        var address = parsed.FirstOrDefault(field => field.Kind == "address");
        var ssh = parsed.Where(field => field.Kind == "sshKey").ToArray();
        if (type == CipherType.SshKey && ssh.Length != 1)
            throw Invalid("A native SSH item requires exactly one supported private key.");
        if (type != CipherType.SshKey && ssh.Length > 0)
            throw Invalid("An embedded SSH key cannot be converted into a different item type without review.");
        var expiry = Native(parsed, "expiry", "monthYear");
        var licenseExpiry = Native(parsed, "expiry_date", "monthYear");
        return new Cipher
        {
            Type = type,
            FolderId = folderId,
            Name = OptionalString(overview, "title") ?? string.Empty,
            Notes = OptionalString(details, "notesPlain"),
            Favorite = OptionalInteger(item, "favIndex") > 0,
            Reprompt = reprompt,
            CreationDate = created ?? default,
            RevisionDate = updated ?? default,
            Fields = fields.ToArray(),
            PasswordHistory = history,
            Login = type == CipherType.Login ? new CipherLogin(username, password,
                parsed.FirstOrDefault(field => field.Kind == "totp")?.Scalar, uris) : null,
            SecureNote = type == CipherType.SecureNote ? new CipherSecureNote(0) : null,
            Card = type == CipherType.Card ? new CipherCard(
                NativeText(parsed, "cardholder"), NativeText(parsed, "ccnum"),
                expiry?.MonthYear?.Month.ToString(CultureInfo.InvariantCulture),
                expiry?.MonthYear?.Year.ToString(CultureInfo.InvariantCulture),
                NativeText(parsed, "cvv"), CardBrand(NativeText(parsed, "type"))) : null,
            Identity = type == CipherType.Identity ? new CipherIdentity(
                NativeText(parsed, "title"), NativeText(parsed, "firstname"), NativeText(parsed, "initial"),
                NativeText(parsed, "lastname"), NativeText(parsed, "username"), NativeText(parsed, "company"),
                NativeText(parsed, "ssn"), null, null, NativeText(parsed, "email"), NativeText(parsed, "defphone"),
                Address(address, "street"), null, null, Address(address, "city"), Address(address, "state"),
                Address(address, "zip"), Address(address, "country")) : null,
            BankAccount = type == CipherType.BankAccount ? new CipherBankAccount(
                NativeText(parsed, "bankName"), NativeText(parsed, "owner"), NativeText(parsed, "accountType"),
                NativeText(parsed, "accountNo"), NativeText(parsed, "routingNo"), NativeText(parsed, "branchNo"),
                NativeText(parsed, "telephonePin"), NativeText(parsed, "swift"), NativeText(parsed, "iban"),
                NativeText(parsed, "branchPhone")) : null,
            DriversLicense = type == CipherType.DriversLicense ? new CipherDriversLicense(
                null, null, null, NativeDate(parsed, "birthdate"), NativeText(parsed, "number"),
                NativeText(parsed, "country"), NativeText(parsed, "state"), null,
                licenseExpiry?.MonthYear is { } month ? new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : NativeDate(parsed, "expiry_date"), null, NativeText(parsed, "class")) : null,
            Passport = type == CipherType.Passport ? new CipherPassport(
                null, null, NativeDate(parsed, "birthdate"), NativeText(parsed, "sex"), NativeText(parsed, "birthplace"),
                NativeText(parsed, "nationality"), NativeText(parsed, "issuing_country"), NativeText(parsed, "number"),
                NativeText(parsed, "type"), null, NativeText(parsed, "issuing_authority"), NativeDate(parsed, "issue_date"),
                NativeDate(parsed, "expiry_date")) : null,
            Ssh = type == CipherType.SshKey ? ssh[0].Ssh : null,
        };
    }

    private static SourceField? ReadField(string id, string label, JsonElement value, bool guarded, List<CipherField> fields)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            Add(fields, label, null, guarded, includeEmpty: true);
            return null;
        }
        Object(value);
        var populated = value.EnumerateObject().Where(property => property.Value.ValueKind != JsonValueKind.Null).ToArray();
        if (populated.Length == 0)
        {
            Add(fields, label, string.Empty, guarded, includeEmpty: true);
            return null;
        }
        if (populated.Length != 1) throw Invalid("A 1Password field has more than one value type.");
        var kind = populated[0].Name;
        var body = populated[0].Value;
        string? scalar;
        SourceField result;
        switch (kind)
        {
            case "string": case "concealed": case "phone": case "menu": case "gender":
            case "url": case "creditCardType": case "creditCardNumber": case "totp":
                scalar = String(body);
                result = new(id, kind, scalar);
                Add(fields, label, scalar, guarded || kind is "concealed" or "creditCardNumber" or "totp", includeEmpty: true);
                return result;
            case "date":
                var date = UnixTime(body);
                scalar = date.ToString("O", CultureInfo.InvariantCulture);
                Add(fields, label, scalar, guarded, includeEmpty: true);
                return new(id, kind, scalar) { Date = date };
            case "monthYear":
                if (body.ValueKind != JsonValueKind.Number || !body.TryGetInt32(out var encoded))
                    throw Invalid("A 1Password month/year value is invalid.");
                if (encoded == 0)
                {
                    Add(fields, label, "0", guarded, includeEmpty: true);
                    return new(id, kind, "0");
                }
                if (encoded / 100 is < 1 or > 9999 || encoded % 100 is < 1 or > 12)
                    throw Invalid("A 1Password month/year value is invalid.");
                var month = (Year: encoded / 100, Month: encoded % 100);
                scalar = month.Year.ToString("D4", CultureInfo.InvariantCulture) + "-" + month.Month.ToString("D2", CultureInfo.InvariantCulture);
                Add(fields, label, scalar, guarded, includeEmpty: true);
                return new(id, kind, scalar) { MonthYear = month };
            case "email":
                Object(body);
                CheckProperties(body, "email_address", "provider");
                scalar = OptionalString(body, "email_address") ?? string.Empty;
                Add(fields, label, scalar, guarded, includeEmpty: true);
                Add(fields, label + " / provider", OptionalString(body, "provider"), guarded);
                return new(id, kind, scalar);
            case "address":
                Object(body);
                CheckProperties(body, "street", "city", "country", "zip", "state");
                var address = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var component in new[] { "street", "city", "country", "zip", "state" })
                {
                    var componentValue = OptionalString(body, component);
                    address.Add(component, componentValue);
                    Add(fields, label + " / " + component, componentValue, guarded, includeEmpty: componentValue is not null);
                }
                return new(id, kind, null) { Address = address };
            case "sshKey":
                Object(body);
                CheckProperties(body, "privateKey", "metadata");
                var metadata = RequiredObject(body, "metadata");
                CheckProperties(metadata, "privateKey", "publicKey", "fingerprint", "keyType");
                var privateKey = RequiredString(metadata, "privateKey");
                var publicKey = OptionalString(metadata, "publicKey");
                using (var key = SshAgentKey.Parse(privateKey, publicKey))
                {
                    var fingerprint = OptionalString(metadata, "fingerprint");
                    if (!string.IsNullOrEmpty(fingerprint) && fingerprint != key.Fingerprint)
                        throw Invalid("The 1Password SSH fingerprint does not match its private key.");
                    var canonicalPublic = key.Algorithm + " " + Convert.ToBase64String(key.PublicKeyBlob);
                    Add(fields, label + " / private key", privateKey, true, includeEmpty: true);
                    Add(fields, label + " / public key", publicKey, guarded);
                    Add(fields, label + " / key type", OptionalString(metadata, "keyType"), guarded);
                    var originalEncoding = OptionalString(body, "privateKey");
                    if (originalEncoding is not null && originalEncoding != privateKey)
                        Add(fields, label + " / original private-key encoding", originalEncoding, true, includeEmpty: true);
                    return new(id, kind, null) { Ssh = new CipherSsh(privateKey, publicKey ?? canonicalPublic, key.Fingerprint) };
                }
            default:
                throw Invalid("The 1PUX export contains a field type that cannot be imported without losing its meaning.");
        }
    }

    private static IReadOnlyList<CipherLoginUri> ReadUris(JsonElement overview, List<CipherField> fields)
    {
        var result = new List<CipherLoginUri>();
        var primary = OptionalString(overview, "url");
        Add(fields, "1Password primary URL", primary, false);
        if (!string.IsNullOrEmpty(primary)) result.Add(new(primary, null));
        var index = 0;
        foreach (var item in OptionalArray(overview, "urls"))
        {
            Object(item);
            CheckProperties(item, "label", "url");
            var url = OptionalString(item, "url") ?? string.Empty;
            if (!result.Any(existing => existing.Uri == url)) result.Add(new(url, null));
            Add(fields, "1Password URL " + (++index).ToString(CultureInfo.InvariantCulture) + " label", OptionalString(item, "label"), false, includeEmpty: true);
            Add(fields, "1Password URL " + index.ToString(CultureInfo.InvariantCulture), url, false, includeEmpty: true);
        }
        if (result.Count > 1_024) throw Invalid("A 1Password item contains too many URLs.");
        return result;
    }

    private static IReadOnlyList<PasswordHistoryEntry> ReadHistory(JsonElement details)
    {
        var result = new List<PasswordHistoryEntry>();
        foreach (var entry in OptionalArray(details, "passwordHistory"))
        {
            if (result.Count >= MaximumHistoryEntries) throw Invalid("A 1Password item contains too much password history.");
            Object(entry);
            CheckProperties(entry, "value", "time");
            result.Add(new PasswordHistoryEntry(OptionalString(entry, "value") ?? string.Empty, OptionalUnixTime(entry, "time")));
        }
        return result;
    }

    private static SourceField? Native(IReadOnlyList<SourceField> fields, string id, string? kind = null)
    {
        var matches = fields.Where(field => field.Id == id && (kind is null || field.Kind == kind)).ToArray();
        if (matches.Length > 1 && matches.Skip(1).Any(field => field.Scalar != matches[0].Scalar))
            throw Invalid("The 1Password item has ambiguous native fields.");
        return matches.FirstOrDefault();
    }

    private static string? NativeText(IReadOnlyList<SourceField> fields, string id) => Native(fields, id)?.Scalar;
    private static string? NativeLoginText(IReadOnlyList<SourceField> fields, string id, params string[] kinds)
    {
        var candidates = fields.Where(field => field.Id == id && kinds.Contains(field.Kind, StringComparer.Ordinal)).ToArray();
        if (candidates.Length > 1 && candidates.Skip(1).Any(field => field.Scalar != candidates[0].Scalar))
            throw Invalid("The 1Password login contains ambiguous section credentials.");
        return candidates.FirstOrDefault()?.Scalar;
    }
    private static string? NativeDate(IReadOnlyList<SourceField> fields, string id) => Native(fields, id, "date")?.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string? Address(SourceField? field, string component) => field?.Address?.GetValueOrDefault(component);
    private static string? CardBrand(string? source) => source?.ToLowerInvariant() switch
    {
        "visa" => "Visa", "mastercard" or "mc" => "Mastercard", "amex" or "american express" => "Amex",
        "discover" => "Discover", "diners" or "diners club" => "Diners Club", "jcb" => "JCB", "unionpay" => "UnionPay",
        _ => source,
    };

    private static void Add(List<CipherField> fields, string label, string? value, bool secret, bool includeEmpty = false)
    {
        if (!includeEmpty && string.IsNullOrEmpty(value)) return;
        if (fields.Count >= MaximumFieldsPerItem) throw Invalid("A 1Password item contains too many fields.");
        fields.Add(new CipherField(label, value, secret ? CipherFieldType.Hidden : CipherFieldType.Text));
    }

    private static JsonElement Object(JsonElement element) => element.ValueKind == JsonValueKind.Object ? element : throw Invalid("The 1PUX export contains an invalid object.");
    private static JsonElement RequiredObject(JsonElement parent, string property) => parent.TryGetProperty(property, out var value) ? Object(value) : throw Invalid("The 1PUX export is missing required data.");
    private static string RequiredString(JsonElement parent, string property) => OptionalString(parent, property) ?? throw Invalid("The 1PUX export is missing required text.");
    private static string Identifier(JsonElement parent, string property)
    {
        var value = RequiredString(parent, property);
        if (value.Length is 0 or > 128 || value.Any(char.IsControl)) throw Invalid("The 1PUX export contains an invalid identifier.");
        return value;
    }
    private static string? OptionalString(JsonElement parent, string property) => !parent.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null ? null : String(value);
    private static string String(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid("The 1PUX export contains a non-text value where text is required.");
        var text = value.GetString()!;
        if (text.Length > MaximumStringCharacters) throw Invalid("A 1PUX text field exceeds the supported size limit.");
        return text;
    }
    private static IEnumerable<JsonElement> OptionalArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Array) throw Invalid("The 1PUX export contains an invalid array.");
        return value.EnumerateArray();
    }
    private static bool OptionalBoolean(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("The 1PUX export contains an invalid flag.");
        return value.GetBoolean();
    }
    private static long OptionalInteger(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) throw Invalid("The 1PUX export contains an invalid number.");
        return number;
    }
    private static DateTimeOffset? OptionalUnixTime(JsonElement parent, string property) => !parent.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null ? null : UnixTime(value);
    private static DateTimeOffset UnixTime(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) throw Invalid("A 1PUX timestamp is invalid.");
        try { return DateTimeOffset.FromUnixTimeSeconds(number); }
        catch (ArgumentOutOfRangeException exception) { throw Invalid("A 1PUX timestamp is outside the supported range.", exception); }
    }
    private static bool HasContent(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.String => value.GetString()!.Length > 0,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        JsonValueKind.Array => value.GetArrayLength() > 0,
        _ => true,
    };
    private static void CheckProperties(JsonElement element, params string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) && HasContent(property.Value))
                throw Invalid("The 1PUX export contains unrecognized data that cannot be discarded safely.");
    }
    private static ReadOnlyMemory<byte> WithoutBom(byte[] bytes) => bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? bytes.AsMemory(3) : bytes;
    private static void ValidateJson(JsonElement root, CancellationToken ct)
    {
        var visited = 0;
        Visit(root);
        void Visit(JsonElement value)
        {
            if (++visited > MaximumJsonNodes) throw Invalid("The 1PUX JSON contains too many values.");
            if ((visited & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw Invalid("The 1PUX JSON contains duplicate properties.");
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var child in value.EnumerateArray()) Visit(child);
        }
    }

    private sealed record SourceField(string Id, string Kind, string? Scalar)
    {
        public DateTimeOffset? Date { get; init; }
        public (int Year, int Month)? MonthYear { get; init; }
        public IReadOnlyDictionary<string, string?>? Address { get; init; }
        public CipherSsh? Ssh { get; init; }
    }

    private sealed record EntryHeader(string Name, long Length, long CompressedLength, uint Crc,
        bool Directory, long LocalOffset, long DataStart, long DataEnd, ushort Method);

    private static IReadOnlyList<EntryHeader> ReadCentralDirectory(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 22 || Read32(bytes, 0) != 0x04034b50)
            throw Invalid("The selected file is not a supported ZIP archive.");
        var end = -1;
        for (var index = bytes.Length - 22; index >= Math.Max(0, bytes.Length - 65_557); index--)
        {
            if (Read32(bytes, index) == 0x06054b50 && index + 22 + Read16(bytes, index + 20) == bytes.Length)
            {
                end = index;
                break;
            }
        }
        if (end < 0 || Read16(bytes, end + 4) != 0 || Read16(bytes, end + 6) != 0
            || Read16(bytes, end + 8) != Read16(bytes, end + 10))
            throw Invalid("Split or incomplete ZIP archives are not supported.");
        var count = Read16(bytes, end + 10);
        var directoryLength = Read32(bytes, end + 12);
        var directoryStart = Read32(bytes, end + 16);
        if (count is 0 or > MaximumArchiveEntries || directoryLength == uint.MaxValue || directoryStart == uint.MaxValue
            || (long)directoryStart + directoryLength != end)
            throw Invalid("ZIP64 or oversized ZIP directories are not supported.");
        var position = checked((int)directoryStart);
        var entries = new List<EntryHeader>(count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalLength = 0;
        while (position < end)
        {
            if (entries.Count >= count || position > end - 46 || Read32(bytes, position) != 0x02014b50)
                throw Invalid("The ZIP directory is inconsistent.");
            var flags = Read16(bytes, position + 8);
            var method = Read16(bytes, position + 10);
            var crc = Read32(bytes, position + 16);
            var compressed = Read32(bytes, position + 20);
            var length = Read32(bytes, position + 24);
            var nameLength = Read16(bytes, position + 28);
            var extraLength = Read16(bytes, position + 30);
            var commentLength = Read16(bytes, position + 32);
            var attributes = Read32(bytes, position + 38);
            var local = Read32(bytes, position + 42);
            var recordLength = 46L + nameLength + extraLength + commentLength;
            if (recordLength > end - position || nameLength is 0 or > 1_024 || Read16(bytes, position + 34) != 0
                || (flags & 0x0041) != 0 || method is not (0 or 8) || length > MaximumDataBytes
                || compressed == uint.MaxValue || local == uint.MaxValue
                || ((attributes >> 16) & 0xf000) == 0xa000 || (attributes & 0x400) != 0)
                throw Invalid("The ZIP archive contains unsupported or oversized entries.");
            var nameBytes = bytes.Slice(position + 46, nameLength);
            if ((flags & 0x0800) == 0 && nameBytes.ContainsAnyExceptInRange((byte)0, (byte)0x7f))
                throw Invalid("ZIP entry names must use UTF-8.");
            string name;
            try { name = StrictUtf8.GetString(nameBytes); }
            catch (DecoderFallbackException exception) { throw Invalid("A ZIP entry name contains invalid UTF-8.", exception); }
            ValidateEntryName(name);
            if (!names.Add(name)) throw Invalid("The ZIP archive contains duplicate entry names.");
            var directory = name.EndsWith('/');
            if ((directory && (length != 0 || crc != 0))
                || length > (long)MaximumDataBytes + MaximumAttributesBytes - totalLength
                || length > Math.Max(1L, compressed) * 1_000L)
                throw Invalid("The ZIP archive exceeds its supported decompression limits.");
            totalLength += length;
            if ((long)local + 30 > directoryStart
                || Read32(bytes, checked((int)local)) != 0x04034b50)
                throw Invalid("A ZIP local header is invalid.");
            var localPosition = checked((int)local);
            var localNameLength = Read16(bytes, localPosition + 26);
            var localExtraLength = Read16(bytes, localPosition + 28);
            var dataStart = (long)local + 30 + localNameLength + localExtraLength;
            var dataEnd = dataStart + compressed;
            if (dataEnd > directoryStart || Read16(bytes, localPosition + 8) != method
                || Read16(bytes, localPosition + 6) != flags || localNameLength != nameLength
                || !bytes.Slice(localPosition + 30, localNameLength).SequenceEqual(nameBytes)
                || (flags & 8) == 0 && (Read32(bytes, localPosition + 14) != crc
                    || Read32(bytes, localPosition + 18) != compressed || Read32(bytes, localPosition + 22) != length))
                throw Invalid("ZIP local and central entry metadata do not agree.");
            if ((flags & 8) != 0)
            {
                if (!LocalDescriptorValue(Read32(bytes, localPosition + 14), crc)
                    || !LocalDescriptorValue(Read32(bytes, localPosition + 18), compressed)
                    || !LocalDescriptorValue(Read32(bytes, localPosition + 22), length))
                    throw Invalid("A ZIP local descriptor placeholder is inconsistent.");
                dataEnd = DescriptorEnd(bytes, dataEnd, directoryStart, crc, compressed, length);
            }
            entries.Add(new EntryHeader(name, length, compressed, crc, directory, local, dataStart, dataEnd, method));
            position += checked((int)recordLength);
        }
        if (entries.Count != count) throw Invalid("The ZIP entry count is inconsistent.");
        long previousEnd = 0;
        foreach (var entry in entries.OrderBy(entry => entry.LocalOffset))
        {
            if (entry.LocalOffset < previousEnd) throw Invalid("ZIP entries overlap.");
            previousEnd = entry.DataEnd;
        }
        return entries;
    }

    private static bool LocalDescriptorValue(uint local, uint central) => local == 0 || local == central;

    private static long DescriptorEnd(ReadOnlySpan<byte> bytes, long position, long limit, uint crc, uint compressed, uint length)
    {
        if (position + 12 > limit)
            throw Invalid("A ZIP data descriptor is truncated.");
        var index = checked((int)position);
        if (position + 16 <= limit && Read32(bytes, index) == 0x08074b50
            && MatchesDescriptor(bytes, index + 4, crc, compressed, length))
            return position + 16;
        if (MatchesDescriptor(bytes, index, crc, compressed, length))
            return position + 12;
        throw Invalid("A ZIP data descriptor does not match the central directory.");
    }

    private static bool MatchesDescriptor(ReadOnlySpan<byte> bytes, int position, uint crc, uint compressed, uint length) =>
        Read32(bytes, position) == crc && Read32(bytes, position + 4) == compressed && Read32(bytes, position + 8) == length;

    private static void ValidateEntryName(string name)
    {
        if (name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl))
            throw Invalid("The ZIP archive contains an unsafe entry path.");
        var path = name.EndsWith('/') ? name[..^1] : name;
        if (path.Split('/').Any(component => component is "" or "." or ".."))
            throw Invalid("The ZIP archive contains an unsafe entry path.");
    }

    private static void ValidateDirectoryPayload(ReadOnlyMemory<byte> archiveBytes, EntryHeader header, CancellationToken ct)
    {
        if (header.Method == 0)
        {
            if (header.CompressedLength != 0)
                throw Invalid("A stored ZIP directory must not contain data.");
            return;
        }
        if (header.CompressedLength is < 2 or > 1_024)
            throw Invalid("A compressed ZIP directory exceeds the supported empty-entry size.");
        var compressed = archiveBytes.Slice(checked((int)header.DataStart), checked((int)header.CompressedLength)).ToArray();
        try
        {
            ct.ThrowIfCancellationRequested();
            using var memory = new MemoryStream(compressed, writable: false);
            using var inflater = new DeflateStream(memory, CompressionMode.Decompress);
            if (inflater.ReadByte() != -1)
                throw Invalid("A ZIP directory contains an undeclared body.");
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(compressed);
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, EntryHeader header, int maximum, CancellationToken ct)
    {
        if (header.Length > maximum) throw Invalid("A 1PUX data file exceeds the supported size limit.");
        var content = new byte[checked((int)header.Length)];
        try
        {
            using var stream = entry.Open();
            var offset = 0;
            uint crc = uint.MaxValue;
            while (offset < content.Length)
            {
                ct.ThrowIfCancellationRequested();
                var read = stream.Read(content, offset, Math.Min(81_920, content.Length - offset));
                if (read == 0) throw Invalid("A ZIP entry ended before its declared length.");
                foreach (var value in content.AsSpan(offset, read))
                    crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
                offset += read;
            }
            if (stream.ReadByte() != -1 || (crc ^ uint.MaxValue) != header.Crc)
                throw Invalid("A ZIP entry failed its size or CRC integrity check.");
            return content;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(content);
            throw;
        }
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }
    private static ushort Read16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
    private static uint Read32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    private static InvalidDataException Invalid(string message, Exception? inner = null) => new(message, inner);
}
