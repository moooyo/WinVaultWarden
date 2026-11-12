using System.Globalization;
using System.Text;
using Core.Enums;
using Core.Models;
using Core.Services;

namespace Vault.Porting;

public enum BrowserCsvSource
{
    Chrome,
    Edge,
    Firefox,
    LastPass,
}

/// <summary>
/// Imports the documented Chromium, Firefox and LastPass CSV column families.
/// Values are never trimmed. Unsupported nonempty columns reject the complete document.
/// Firefox metadata and optional LastPass CLI metadata are retained as hidden fields.
/// </summary>
/// <remarks>
/// Contract references:
/// https://github.com/chromium/chromium/blob/main/components/password_manager/core/browser/export/password_csv_writer.cc
/// https://github.com/mozilla-firefox/firefox/blob/main/toolkit/components/passwordmgr/LoginExport.sys.mjs
/// https://github.com/lastpass/lastpass-cli/blob/master/cmd-export.c
/// Edge shares the Chromium importer in the official Bitwarden client. LastPass TOTP is
/// an optional column recognized by that client's LastPass CSV importer.
/// </remarks>
public static class BrowserCsvImportCodec
{
    public const int MaximumRecords = 100_000;
    public const int MaximumColumns = 64;
    public const int MaximumCellCharacters = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly string[] ChromiumRequired = ["name", "url", "username", "password"];
    private static readonly string[] ChromiumColumns = ["name", "url", "username", "password", "note"];
    private static readonly string[] FirefoxRequired = ["username", "password"];
    private static readonly string[] FirefoxMetadata =
        ["httpRealm", "formActionOrigin", "guid", "timeCreated", "timeLastUsed", "timePasswordChanged"];
    private static readonly string[] FirefoxColumns =
        ["url", "hostname", "username", "password", .. FirefoxMetadata];
    private static readonly string[] LastPassRequired =
        ["url", "username", "password", "extra", "name", "grouping", "fav"];
    private static readonly string[] LastPassMetadata =
        ["id", "group", "fullname", "last_touch", "last_modified_gmt", "attachpresent"];
    private static readonly string[] LastPassColumns = [.. LastPassRequired, "totp", .. LastPassMetadata];

    public static PortingData Parse(BrowserCsvSource source, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        VaultPortingLimits.ValidateContent(content);
        try
        {
            if ((ulong)StrictUtf8.GetByteCount(content) > VaultPortingLimits.MaximumFileSizeBytes)
                throw new FormatException("The CSV exceeds the supported UTF-8 size limit.");
        }
        catch (EncoderFallbackException)
        {
            throw new FormatException("The CSV contains invalid Unicode text.");
        }
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source));

        var rows = ReadCsv(content);
        if (rows.Count == 0)
            throw new FormatException("The selected CSV is empty or has no header.");

        var header = ReadHeader(rows[0]);
        var required = source switch
        {
            BrowserCsvSource.Chrome or BrowserCsvSource.Edge => ChromiumRequired,
            BrowserCsvSource.Firefox => FirefoxRequired,
            BrowserCsvSource.LastPass => LastPassRequired,
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        foreach (var column in required)
            if (!header.ContainsKey(column))
                throw new FormatException("The CSV header does not match the selected source's export format.");
        if (source == BrowserCsvSource.Firefox && !header.ContainsKey("url") && !header.ContainsKey("hostname"))
            throw new FormatException("The Firefox CSV is missing its URL column.");

        var allowed = new HashSet<string>(source switch
        {
            BrowserCsvSource.Chrome or BrowserCsvSource.Edge => ChromiumColumns,
            BrowserCsvSource.Firefox => FirefoxColumns,
            BrowserCsvSource.LastPass => LastPassColumns,
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        }, StringComparer.OrdinalIgnoreCase);
        var unknown = header.Where(column => !allowed.Contains(column.Key)).Select(column => column.Value).ToArray();
        var ciphers = new List<Cipher>(rows.Count - 1);
        var folders = new List<Folder>();
        var relations = new List<(int CipherIndex, int FolderIndex)>();
        var folderIndices = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 1; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Length == 1 && row[0].Length == 0)
                continue;
            if (row.Length != header.Count)
                throw InvalidRecord(index + 1, "The number of fields does not match the header.");
            if (row.All(string.IsNullOrEmpty))
                continue;
            if (unknown.Any(column => row[column].Length != 0))
                throw InvalidRecord(index + 1, "An unsupported nonempty column would be lost.");

            string Cell(string name) => header.TryGetValue(name, out var column) ? row[column] : string.Empty;
            var fields = new List<CipherField>();
            var url = Cell("url");
            var username = Cell("username");
            var password = Cell("password");
            var name = Cell("name");
            var notes = source == BrowserCsvSource.LastPass ? Cell("extra") : Cell("note");
            var totp = source == BrowserCsvSource.LastPass ? Cell("totp") : string.Empty;
            var creation = default(DateTimeOffset);
            DateTimeOffset? passwordRevision = null;
            var favorite = false;
            string? folderId = null;

            if (source == BrowserCsvSource.Firefox)
            {
                var legacyUrl = Cell("hostname");
                if (url.Length != 0 && legacyUrl.Length != 0 && !string.Equals(url, legacyUrl, StringComparison.Ordinal))
                    throw InvalidRecord(index + 1, "The URL and legacy hostname columns disagree.");
                if (url.Length == 0)
                    url = legacyUrl;
                name = NameFromUrl(url);
                foreach (var metadata in FirefoxMetadata)
                    AddMetadata(fields, "Firefox", metadata, Cell(metadata));
                creation = ReadFirefoxDate(Cell("timeCreated"), index + 1) ?? default;
                _ = ReadFirefoxDate(Cell("timeLastUsed"), index + 1);
                passwordRevision = ReadFirefoxDate(Cell("timePasswordChanged"), index + 1);
            }
            else if (source == BrowserCsvSource.LastPass)
            {
                favorite = ReadBoolean(Cell("fav"), index + 1);
                if (Cell("attachpresent").Length != 0 && ReadBoolean(Cell("attachpresent"), index + 1))
                    throw InvalidRecord(index + 1, "The export reports attachments that CSV cannot contain. Export those files separately before importing.");
                if (url == "http://group")
                    throw InvalidRecord(index + 1, "An internal LastPass group record is not a vault item.");
                foreach (var metadata in LastPassMetadata)
                    AddMetadata(fields, "LastPass", metadata, Cell(metadata));

                var grouping = Cell("grouping");
                if (grouping == "(none)")
                    AddMetadata(fields, "LastPass", "grouping", grouping);
                else if (grouping.Length != 0)
                {
                    if (!folderIndices.TryGetValue(grouping, out var folderIndex))
                    {
                        folderIndex = folders.Count;
                        folderIndices.Add(grouping, folderIndex);
                        folders.Add(new Folder { Id = folderIndex.ToString(CultureInfo.InvariantCulture), Name = grouping });
                    }
                    folderId = folderIndex.ToString(CultureInfo.InvariantCulture);
                    relations.Add((ciphers.Count, folderIndex));
                }
            }

            var isNote = source == BrowserCsvSource.LastPass && url == "http://sn";
            if (isNote)
            {
                // LastPass stores specialized notes as a structured, multiline extra value.
                // Retain that complete value instead of guessing at or discarding its fields.
                AddMetadata(fields, "LastPass", "username", username);
                AddMetadata(fields, "LastPass", "password", password);
                AddMetadata(fields, "LastPass", "totp", totp);
            }
            ciphers.Add(new Cipher
            {
                Type = isNote ? CipherType.SecureNote : CipherType.Login,
                Name = name,
                Notes = EmptyToNull(notes),
                FolderId = folderId,
                Favorite = favorite,
                CreationDate = creation,
                Login = isNote ? null : new CipherLogin(username, password, EmptyToNull(totp),
                    url.Length == 0 ? [] : [new CipherLoginUri(url, null)])
                {
                    PasswordRevisionDate = passwordRevision,
                },
                SecureNote = isNote ? new CipherSecureNote(0) : null,
                Fields = fields,
            });
        }
        return new PortingData(ciphers, folders, relations);
    }

    private static Dictionary<string, int> ReadHeader(string[] cells)
    {
        var header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < cells.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(cells[index]) || !header.TryAdd(cells[index], index))
                throw new FormatException("The CSV header has an empty or duplicate column.");
        }
        return header;
    }

    private static void AddMetadata(List<CipherField> fields, string source, string name, string value)
    {
        if (value.Length != 0)
            fields.Add(new CipherField($"{source}: {name}", value, CipherFieldType.Hidden));
    }

    private static DateTimeOffset? ReadFirefoxDate(string value, int row)
    {
        if (value.Length == 0)
            return null;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds))
            throw InvalidRecord(row, "A Firefox timestamp is not an integer millisecond value.");
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw InvalidRecord(row, "A Firefox timestamp is outside the supported date range.");
        }
    }

    private static bool ReadBoolean(string value, int row) => value switch
    {
        "" or "0" or "false" => false,
        "1" or "true" => true,
        _ => throw InvalidRecord(row, "A boolean export field has an unsupported value."),
    };

    private static string NameFromUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Host.Length != 0 ? parsed.Host : url;

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private static FormatException InvalidRecord(int row, string reason) =>
        new($"CSV record {row.ToString(CultureInfo.InvariantCulture)} is invalid. {reason}");

    private static List<string[]> ReadCsv(string content)
    {
        var rows = new List<string[]>();
        var cells = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        var afterQuote = false;
        var recordStarted = false;
        var index = content.Length != 0 && content[0] == '\uFEFF' ? 1 : 0;

        void Append(char character)
        {
            if (value.Length == MaximumCellCharacters)
                throw new FormatException("A CSV field exceeds the supported size limit.");
            value.Append(character);
        }

        void EndCell()
        {
            if (cells.Count == MaximumColumns)
                throw new FormatException("The CSV has too many columns.");
            cells.Add(value.ToString());
            value.Clear();
            afterQuote = false;
        }

        void EndRecord()
        {
            EndCell();
            if (rows.Count == MaximumRecords + 1)
                throw new FormatException("The CSV has too many records.");
            rows.Add(cells.ToArray());
            cells.Clear();
            recordStarted = false;
        }

        while (index < content.Length)
        {
            var character = content[index++];
            if (quoted)
            {
                if (character != '"')
                {
                    Append(character);
                    continue;
                }
                if (index < content.Length && content[index] == '"')
                {
                    Append('"');
                    index++;
                    continue;
                }
                quoted = false;
                afterQuote = true;
                continue;
            }

            if (afterQuote && character is not (',' or '\r' or '\n'))
                throw new FormatException("A quoted CSV field has trailing characters.");
            switch (character)
            {
                case '"':
                    if (value.Length != 0 || afterQuote)
                        throw new FormatException("A CSV quote appears inside an unquoted field.");
                    quoted = true;
                    recordStarted = true;
                    break;
                case ',':
                    EndCell();
                    recordStarted = true;
                    break;
                case '\r':
                    if (index < content.Length && content[index] == '\n') index++;
                    EndRecord();
                    break;
                case '\n':
                    EndRecord();
                    break;
                default:
                    Append(character);
                    recordStarted = true;
                    break;
            }
        }
        if (quoted)
            throw new FormatException("A quoted CSV field is not terminated.");
        if (recordStarted || afterQuote || cells.Count != 0 || value.Length != 0)
            EndRecord();
        return rows;
    }
}
