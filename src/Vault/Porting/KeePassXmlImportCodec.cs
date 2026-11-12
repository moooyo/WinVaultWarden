using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault.Porting;

public sealed record KeePassXmlImportSummary(
    PortingData Data,
    int ExcludedRecycleBinEntries,
    int HistoricalVersions);

/// <summary>
/// Imports KeePass 2's plaintext XML export, not an encrypted KDBX file or protected
/// inner XML. Standard strings and protected custom fields follow KeePass and the
/// official Bitwarden KeePass importer. Binary attachments are rejected explicitly.
/// </summary>
/// <remarks>
/// Format references: https://keepass.info/help/base/placeholders.html#otp;
/// https://github.com/bitwarden/clients/blob/main/libs/importer/src/importers/keepass2-xml-importer.ts;
/// https://github.com/keepassxreboot/keepassxc/blob/develop/src/format/KdbxXmlWriter.cpp.
/// </remarks>
public static class KeePassXmlImportCodec
{
    public const int MaximumDepth = 64;
    public const int MaximumEntries = 10_000;
    public const int MaximumFolders = 10_000;
    public const int MaximumFolderPathCharacters = 1_024;
    private const int MaximumElements = 250_000;
    private const int MaximumFieldsPerEntry = 256;
    private const int MaximumHistoryPerEntry = 1_000;
    private const int MaximumValueCharacters = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> StandardStrings = new(StringComparer.Ordinal)
    {
        "Title", "UserName", "Password", "URL", "Notes",
    };
    private static readonly HashSet<string> KnownEntryElements = new(StringComparer.Ordinal)
    {
        "UUID", "IconID", "CustomIconUUID", "ForegroundColor", "BackgroundColor", "OverrideURL",
        "Tags", "Times", "String", "Binary", "AutoType", "History", "CustomData", "QualityCheck",
        "PreviousParentGroup",
    };

    public static PortingData Parse(string xml, bool includeRecycleBin = false) =>
        ParseWithSummary(xml, includeRecycleBin).Data;

    public static KeePassXmlImportSummary ParseWithSummary(string xml, bool includeRecycleBin = false)
    {
        ArgumentNullException.ThrowIfNull(xml);
        VaultPortingLimits.ValidateContent(xml);
        try
        {
            if ((ulong)StrictUtf8.GetByteCount(xml) > VaultPortingLimits.MaximumFileSizeBytes)
                throw Invalid("The KeePass export exceeds the supported file size.");
            if (xml.StartsWith('\uFEFF'))
                xml = xml[1..];
            ValidateXmlEnvelope(xml);
            using var source = new StringReader(xml);
            using var reader = XmlReader.Create(source, ReaderSettings());
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root?.Name != "KeePassFile")
                throw Invalid("Select a plaintext KeePass 2 XML export containing KeePassFile.");
            var vaultRoot = Single(root, "Root", required: true)!;
            var groups = vaultRoot.Elements("Group").ToArray();
            if (groups.Length != 1)
                throw Invalid("The KeePass export must contain one root group.");
            var metadata = Single(root, "Meta", required: false);
            var recycleBin = ReadUuid(Single(metadata, "RecycleBinUUID", required: false)?.Value);
            var state = new ParseState(recycleBin, includeRecycleBin);
            state.Visit(groups[0], isRoot: true, prefix: string.Empty);
            return new(new PortingData(state.Ciphers, state.Folders, state.Relations),
                state.ExcludedRecycleBinEntries, state.HistoricalVersions);
        }
        catch (XmlException exception)
        {
            throw new FormatException("The selected file is not valid, bounded plaintext KeePass 2 XML.", exception);
        }
        catch (EncoderFallbackException exception)
        {
            throw new FormatException("The KeePass export contains invalid text encoding.", exception);
        }
    }

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = VaultPortingLimits.MaximumContentCharacters,
        MaxCharactersFromEntities = 1_024,
        CheckCharacters = true,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = false,
    };

    private static void ValidateXmlEnvelope(string xml)
    {
        using var source = new StringReader(xml);
        using var reader = XmlReader.Create(source, ReaderSettings());
        var elements = 0;
        while (reader.Read())
        {
            if (reader.Depth > MaximumDepth)
                throw Invalid("The KeePass export exceeds the supported nesting depth.");
            if (reader.NodeType != XmlNodeType.Element)
                continue;
            if (++elements > MaximumElements)
                throw Invalid("The KeePass export contains too many XML elements.");
            if (reader.GetAttribute("Protected") is { } protectedValue && ReadBoolean(protectedValue))
                throw Invalid("The file contains encrypted protected XML values. Export plaintext XML from KeePass before importing.");
        }
    }

    private sealed class ParseState(string? recycleBin, bool includeRecycleBin)
    {
        private readonly HashSet<string> _groupUuids = new(StringComparer.Ordinal);
        public List<Cipher> Ciphers { get; } = [];
        public List<Folder> Folders { get; } = [];
        public List<(int CipherIndex, int FolderIndex)> Relations { get; } = [];
        public int ExcludedRecycleBinEntries { get; private set; }
        public int HistoricalVersions { get; private set; }

        public void Visit(XElement group, bool isRoot, string prefix)
        {
            var uuid = ReadUuid(Single(group, "UUID", required: false)?.Value);
            if (uuid is not null && !_groupUuids.Add(uuid))
                throw Invalid("The KeePass export contains duplicate group identifiers.");
            if (!includeRecycleBin && uuid is not null && uuid == recycleBin)
            {
                ExcludedRecycleBinEntries += CountCurrentEntries(group);
                return;
            }
            var name = Single(group, "Name", required: false)?.Value ?? string.Empty;
            CheckValue(name);
            var path = isRoot ? prefix : string.IsNullOrEmpty(prefix)
                ? (name.Length == 0 ? "(unnamed group)" : name)
                : prefix + "/" + (name.Length == 0 ? "(unnamed group)" : name);
            if (path.Length > MaximumFolderPathCharacters)
                throw Invalid("A KeePass folder path exceeds the supported size.");
            int? folderIndex = null;
            if (!isRoot)
            {
                if (Folders.Count >= MaximumFolders)
                    throw Invalid("The KeePass export contains too many folders.");
                folderIndex = Folders.Count;
                Folders.Add(new Folder
                {
                    Id = folderIndex.Value.ToString(CultureInfo.InvariantCulture),
                    Name = path,
                    RevisionDate = ReadTime(Single(Single(group, "Times", false), "LastModificationTime", false)) ?? default,
                });
            }
            var groupNotes = Single(group, "Notes", required: false)?.Value;
            if (!string.IsNullOrEmpty(groupNotes))
            {
                CheckValue(groupNotes);
                Add(new Cipher
                {
                    Type = CipherType.SecureNote,
                    Name = (isRoot ? (name.Length == 0 ? "KeePass" : name) : path) + " (group notes)",
                    Notes = groupNotes,
                    SecureNote = new CipherSecureNote(0),
                }, folderIndex);
            }
            foreach (var entry in group.Elements("Entry"))
                Add(ReadEntry(entry), folderIndex);
            foreach (var child in group.Elements("Group"))
                Visit(child, isRoot: false, path);
        }

        private void Add(Cipher cipher, int? folderIndex)
        {
            if (Ciphers.Count >= MaximumEntries)
                throw Invalid("The KeePass export contains too many entries.");
            var index = Ciphers.Count;
            Ciphers.Add(cipher.WithFolderId(folderIndex?.ToString(CultureInfo.InvariantCulture)));
            if (folderIndex is { } folder)
                Relations.Add((index, folder));
        }

        private Cipher ReadEntry(XElement entry)
        {
            if (entry.Elements("Binary").Any())
                throw Invalid("KeePass binary attachments cannot be imported without losing their contents. Remove or separately preserve attachments before importing.");
            if (entry.Elements().Any(child => child.Name.NamespaceName.Length != 0
                    || !KnownEntryElements.Contains(child.Name.LocalName)))
                throw Invalid("The KeePass entry contains an unsupported field structure.");
            var values = ReadStrings(entry);
            foreach (var key in new[] { "UserName", "Password", "URL" })
            {
                if (Read(values, key) is { } value && HasUnresolvedCredentialReference(value))
                    throw Invalid("The KeePass entry uses credential references or placeholders. Resolve those values in KeePass before importing.");
            }
            var fields = values.Where(pair => !StandardStrings.Contains(pair.Key))
                .Select(pair => new CipherField(pair.Key, pair.Value.Value,
                    pair.Value.Hidden || IsOtpField(pair.Key) ? CipherFieldType.Hidden : CipherFieldType.Text))
                .ToList();
            PreserveScalar(entry, fields, "Tags", "KeePass tags", CipherFieldType.Text);
            PreserveScalar(entry, fields, "OverrideURL", "KeePass override URL", CipherFieldType.Text);
            PreserveXml(entry, fields, "AutoType", "KeePass auto-type configuration");
            PreserveXml(entry, fields, "CustomData", "KeePass custom data");

            var times = Single(entry, "Times", false);
            foreach (var time in times?.Elements() ?? [])
            {
                if (time.HasElements || time.Name.NamespaceName.Length != 0)
                    throw Invalid("The KeePass export contains an unsupported time structure.");
                if (time.Name.LocalName is "CreationTime" or "LastModificationTime")
                    continue;
                AddField(fields, "KeePass time: " + time.Name.LocalName, time.Value, CipherFieldType.Text);
            }
            var history = new List<PasswordHistoryEntry>();
            var historyCount = 0;
            foreach (var version in Single(entry, "History", false)?.Elements("Entry") ?? [])
            {
                if (++historyCount > MaximumHistoryPerEntry)
                    throw Invalid("The KeePass entry contains too much password history.");
                HistoricalVersions++;
                var previous = ReadStrings(version);
                if (previous.TryGetValue("Password", out var password))
                    history.Add(new(password.Value,
                        ReadTime(Single(Single(version, "Times", false), "LastModificationTime", false))));
            }
            if (fields.Count > MaximumFieldsPerEntry)
                throw Invalid("The KeePass entry contains too many custom fields.");
            var title = Read(values, "Title");
            return new Cipher
            {
                Id = string.Empty,
                Type = CipherType.Login,
                Name = string.IsNullOrEmpty(title) ? "Untitled KeePass entry" : title,
                Notes = Read(values, "Notes"),
                Login = new(Read(values, "UserName"), Read(values, "Password"), ReadTotp(values),
                    Read(values, "URL") is { Length: > 0 } url ? [new(url, null)] : []),
                CreationDate = ReadTime(Single(times, "CreationTime", false)) ?? default,
                RevisionDate = ReadTime(Single(times, "LastModificationTime", false)) ?? default,
                Fields = fields,
                PasswordHistory = history,
            };
        }
    }

    private sealed record ImportedValue(string Value, bool Hidden);

    private static Dictionary<string, ImportedValue> ReadStrings(XElement entry)
    {
        var values = new Dictionary<string, ImportedValue>(StringComparer.Ordinal);
        foreach (var field in entry.Elements("String"))
        {
            if (values.Count >= MaximumFieldsPerEntry)
                throw Invalid("The KeePass entry contains too many string fields.");
            var keyElement = Single(field, "Key", true)!;
            var valueElement = Single(field, "Value", true)!;
            if (keyElement.HasElements || valueElement.HasElements || keyElement.Value.Length is 0 or > 512)
                throw Invalid("The KeePass export contains a malformed string field.");
            CheckValue(valueElement.Value);
            var hidden = valueElement.Attribute("ProtectInMemory") is { } protection && ReadBoolean(protection.Value);
            if (!values.TryAdd(keyElement.Value, new(valueElement.Value, hidden)))
                throw Invalid("The KeePass entry contains duplicate string keys.");
        }
        return values;
    }

    private static string? Read(IReadOnlyDictionary<string, ImportedValue> values, string key) =>
        values.TryGetValue(key, out var value) ? value.Value : null;

    private static bool IsOtpField(string key) => key.Equals("otp", StringComparison.OrdinalIgnoreCase)
        || key.Contains("otp", StringComparison.OrdinalIgnoreCase);

    private static string? ReadTotp(IReadOnlyDictionary<string, ImportedValue> values)
    {
        var plugin = Read(values, "otp");
        var native = new[] { "TimeOtp-Secret", "TimeOtp-Secret-Hex", "TimeOtp-Secret-Base32", "TimeOtp-Secret-Base64" }
            .Select(name => (Name: name, Value: Read(values, name)))
            .Where(item => !string.IsNullOrEmpty(item.Value)).ToArray();
        if (native.Length > 1 || (native.Length != 0 && !string.IsNullOrEmpty(plugin)))
            throw Invalid("The KeePass entry contains multiple OTP configurations. Choose one before importing.");
        if (native.Length == 0)
            return string.IsNullOrEmpty(plugin) ? null : ReadPluginTotp(plugin);
        var selected = native[0];
        string secret;
        byte[]? bytes = null;
        try
        {
            if (selected.Value!.Length > 4_096)
                throw Invalid("The KeePass OTP secret exceeds the supported size.");
            if (selected.Name == "TimeOtp-Secret-Base32")
                secret = NormalizeBase32(selected.Value);
            else
            {
                bytes = selected.Name switch
                {
                    "TimeOtp-Secret" => StrictUtf8.GetBytes(selected.Value),
                    "TimeOtp-Secret-Hex" => Convert.FromHexString(new string(selected.Value.Where(character => !char.IsWhiteSpace(character)).ToArray())),
                    "TimeOtp-Secret-Base64" => Convert.FromBase64String(selected.Value),
                    _ => throw Invalid("The KeePass OTP secret encoding is unsupported."),
                };
                secret = EncodeBase32(bytes);
            }
            var digits = ReadPositiveParameter(values, "TimeOtp-Length", 6, 8);
            var period = ReadPositiveParameter(values, "TimeOtp-Period", 30, int.MaxValue);
            var algorithm = Read(values, "TimeOtp-Algorithm") switch
            {
                null or "" or "HMAC-SHA-1" => "SHA1",
                "HMAC-SHA-256" => "SHA256",
                "HMAC-SHA-512" => "SHA512",
                _ => throw Invalid("The KeePass OTP algorithm is not supported."),
            };
            return ValidateTotp($"otpauth://totp/KeePass?secret={secret}&algorithm={algorithm}&digits={digits}&period={period}");
        }
        catch (FormatException exception)
        {
            throw new FormatException("The KeePass OTP configuration is invalid or unsupported.", exception);
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string ReadPluginTotp(string value)
    {
        if (value.Length > 4_096)
            throw Invalid("The KeePass OTP configuration exceeds the supported size.");
        if (value.StartsWith("key=", StringComparison.OrdinalIgnoreCase))
        {
            var parameters = ReadQuery(value);
            if (!parameters.TryGetValue("key", out var key)
                || parameters.Keys.Any(name => name.ToLowerInvariant() is not ("key" or "step" or "size")))
                throw Invalid("The KeePass OTP plugin configuration is unsupported.");
            var period = ReadPositiveParameter(parameters, "step", 30, int.MaxValue);
            var digits = ReadPositiveParameter(parameters, "size", 6, 9);
            return ValidateTotp($"otpauth://totp/KeePass?secret={NormalizeBase32(key)}&digits={digits}&period={period}");
        }
        if (value.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
            return ValidateTotp(value);
        if (!value.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
            return ValidateTotp(NormalizeBase32(value));
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("totp", StringComparison.OrdinalIgnoreCase))
            throw Invalid("Only time-based KeePass OTP configurations can be imported.");
        var query = ReadQuery(uri.Query.TrimStart('?'));
        if (!query.TryGetValue("secret", out var secret))
            throw Invalid("The KeePass OTP URI has no secret.");
        if (query.TryGetValue("algorithm", out var algorithm)
            && algorithm.ToUpperInvariant() is not ("SHA1" or "SHA256" or "SHA512"))
            throw Invalid("The KeePass OTP URI uses an unsupported algorithm.");
        _ = ReadPositiveParameter(query, "digits", 6, 9);
        _ = ReadPositiveParameter(query, "period", 30, int.MaxValue);
        if (query.TryGetValue("encoder", out var encoder))
        {
            if (encoder != "steam") throw Invalid("The KeePass OTP encoder is unsupported.");
            if ((algorithm is not null && !algorithm.Equals("SHA1", StringComparison.OrdinalIgnoreCase))
                || ReadPositiveParameter(query, "period", 30, int.MaxValue) != 30)
                throw Invalid("The Steam OTP configuration contains unsupported algorithm or period settings.");
            return ValidateTotp("steam://" + NormalizeBase32(secret));
        }
        return ValidateTotp(value);
    }

    private static string ValidateTotp(string value) => TotpGenerator.TryGenerate(value, 0, out _)
        ? value : throw Invalid("The KeePass OTP configuration is invalid or unsupported.");

    private static int ReadPositiveParameter(IReadOnlyDictionary<string, ImportedValue> values, string name, int fallback, int maximum) =>
        ReadPositive(Read(values, name), fallback, maximum);
    private static int ReadPositiveParameter(IReadOnlyDictionary<string, string> values, string name, int fallback, int maximum) =>
        ReadPositive(values.TryGetValue(name, out var value) ? value : null, fallback, maximum);
    private static int ReadPositive(string? value, int fallback, int maximum) => string.IsNullOrEmpty(value) ? fallback
        : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 && number <= maximum
            ? number : throw Invalid("The KeePass OTP parameters are outside the supported range.");

    private static Dictionary<string, string> ReadQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !result.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1])))
                throw Invalid("The KeePass OTP URI contains ambiguous parameters.");
        }
        return result;
    }

    private static string NormalizeBase32(string value)
    {
        var compact = new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
        var unpadded = compact.TrimEnd('=');
        if (unpadded.Length == 0 || unpadded.Any(character =>
                character is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '2' and <= '7')))
            throw Invalid("The KeePass OTP secret is not valid Base32.");
        return unpadded.ToUpperInvariant();
    }

    private static string EncodeBase32(ReadOnlySpan<byte> bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var bits = 0;
        var buffer = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output.Append(alphabet[(buffer >> bits) & 31]);
            }
        }
        if (bits > 0) output.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    private static void PreserveScalar(XElement entry, List<CipherField> fields, string element, string name, CipherFieldType type)
    {
        if (Single(entry, element, false) is { } value && value.Value.Length != 0)
            AddField(fields, name, value.Value, type);
    }

    private static void PreserveXml(XElement entry, List<CipherField> fields, string element, string name)
    {
        if (Single(entry, element, false) is { HasElements: true } value)
            AddField(fields, name, value.ToString(SaveOptions.DisableFormatting), CipherFieldType.Hidden);
    }

    private static void AddField(List<CipherField> fields, string name, string value, CipherFieldType type)
    {
        CheckValue(value);
        if (fields.Count >= MaximumFieldsPerEntry)
            throw Invalid("The KeePass entry contains too many custom fields.");
        var candidate = name;
        for (var suffix = 2; fields.Any(field => field.Name == candidate); suffix++)
            candidate = name + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
        fields.Add(new(candidate, value, type));
    }

    private static int CountCurrentEntries(XElement group) => group.Elements("Entry").Count()
        + group.Elements("Group").Sum(CountCurrentEntries);

    private static DateTimeOffset? ReadTime(XElement? element)
    {
        if (element is null || string.IsNullOrEmpty(element.Value)) return null;
        if (element.HasElements) throw Invalid("The KeePass export contains an unsupported time structure.");
        var encoded = element.Value;
        if (encoded.Length >= 19 && encoded[4] == '-' && encoded[7] == '-' && encoded[10] == 'T'
            && DateTimeOffset.TryParse(encoded, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            return value;
        byte[]? bytes = null;
        try
        {
            bytes = Convert.FromBase64String(encoded);
            if (bytes.Length == sizeof(ulong))
            {
                var seconds = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
                var maximumSeconds = (ulong)(DateTimeOffset.MaxValue.Ticks / TimeSpan.TicksPerSecond);
                if (seconds <= maximumSeconds)
                    return new DateTimeOffset((long)seconds * TimeSpan.TicksPerSecond, TimeSpan.Zero);
            }
        }
        catch (FormatException) { }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
        throw Invalid("The KeePass export contains an unsupported timestamp format.");
    }

    private static string? ReadUuid(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        if (encoded.Length > 128) throw Invalid("The KeePass export contains an invalid group identifier.");
        byte[]? bytes = null;
        try
        {
            bytes = Convert.FromBase64String(encoded);
            if (bytes.Length != 16) throw Invalid("The KeePass export contains an invalid group identifier.");
            return bytes.AsSpan().IndexOfAnyExcept((byte)0) < 0 ? null : Convert.ToBase64String(bytes);
        }
        catch (FormatException exception)
        {
            throw new FormatException("The KeePass export contains an invalid group identifier.", exception);
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool ReadBoolean(string value) => value.Trim() switch
    {
        "True" or "true" or "1" => true,
        "False" or "false" or "0" => false,
        _ => throw Invalid("The KeePass export contains an invalid protection flag."),
    };

    private static bool HasUnresolvedCredentialReference(string value) =>
        value.Contains("{REF:", StringComparison.OrdinalIgnoreCase)
        || value.Contains("{S:", StringComparison.OrdinalIgnoreCase)
        || new[] { "{USERNAME}", "{PASSWORD}", "{URL}", "{TITLE}", "{NOTES}" }
            .Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static XElement? Single(XElement? parent, string name, bool required)
    {
        if (parent is null)
        {
            if (required) throw Invalid("The KeePass export is missing a required element.");
            return null;
        }
        var matches = parent.Elements(name).Take(2).ToArray();
        if (matches.Length > 1 || (required && matches.Length == 0))
            throw Invalid("The KeePass export contains missing or ambiguous elements.");
        return matches.Length == 0 ? null : matches[0];
    }

    private static void CheckValue(string value)
    {
        if (value.Length > MaximumValueCharacters)
            throw Invalid("A KeePass field exceeds the supported value size.");
    }

    private static FormatException Invalid(string message) => new(message);
}
