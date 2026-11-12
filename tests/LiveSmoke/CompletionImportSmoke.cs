using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Core.Models;
using Core.Services;
using Vault;

/// <summary>
/// Exercises supported source importers through the production preparation,
/// encrypted write and sync/decryption paths. Run only with a disposable account.
/// </summary>
internal static class CompletionImportSmoke
{
    public static async Task RunAsync(
        VaultImportService importService,
        VaultWriteService writeService,
        SyncService sync,
        VaultSession session,
        string uniquePrefix,
        Action<string, bool, string?> step)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniquePrefix);
        var prefix = uniquePrefix + "-formats";
        var sources = BuildSources(prefix);
        var originalCipherIds = session.Ciphers.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        var originalFolderIds = session.Folders.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        var createdCipherIds = new HashSet<string>(StringComparer.Ordinal);
        var createdFolderIds = new HashSet<string>(StringComparer.Ordinal);
        using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = operation.Token;
        try
        {
            var destinationName = prefix + "-destination";
            await writeService.SaveFolderAsync(null, destinationName, ct);
            var destination = session.Folders.Single(value => value.Name == destinationName
                && !originalFolderIds.Contains(value.Id));
            createdFolderIds.Add(destination.Id);

            foreach (var source in sources)
            {
                try
                {
                    var canonical = await importService.PrepareFileAsync(source.Format, source.Bytes, ct);
                    var options = source.Format == ImportFormat.ChromeCsv
                        ? new VaultImportOptions(destination.Id)
                        : new VaultImportOptions();
                    var preview = importService.Parse(ImportFormat.Json, canonical, options);
                    step($"formats/{source.Label}: preparation and preview", preview.Error is null
                        && preview.Ciphers == 1, preview.Error is null ? "one synthetic login" : "preview rejected");
                    if (preview.Error is not null || preview.Ciphers != 1)
                        continue;

                    var imported = await importService.ImportAsync(ImportFormat.Json, canonical, options, ct);
                    step($"formats/{source.Label}: encrypted server import", imported == 1,
                        $"count={imported}");
                    var ciphers = await sync.SyncAsync(ct);
                    CaptureOwned(ciphers);
                    var found = ciphers.Where(value => !originalCipherIds.Contains(value.Id)
                        && value.Name == source.Name
                        && value.Login?.Username == source.Username).ToArray();
                    var cipher = found.Length == 1 ? found[0] : null;
                    step($"formats/{source.Label}: credential round-trip", cipher is not null
                        && cipher.Login!.Password == source.Password,
                        cipher is null ? "one matching imported item was not found" : "name, username and password preserved");
                    if (cipher is null)
                        continue;

                    var expectedFolder = source.Format == ImportFormat.ChromeCsv
                        ? destination.Name
                        : source.FolderName;
                    var actualFolder = session.Folders.FirstOrDefault(value => value.Id == cipher.FolderId)?.Name;
                    step($"formats/{source.Label}: folder relationship", actualFolder == expectedFolder,
                        source.Format == ImportFormat.ChromeCsv ? "explicit destination folder" : "source relationship preserved");
                }
                catch (Exception exception)
                {
                    step($"formats/{source.Label}: complete flow", false, exception.GetType().Name);
                }
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var cleanupOk = true;
            try
            {
                CaptureOwned(await sync.SyncAsync(cleanup.Token));
            }
            catch
            {
                cleanupOk = false;
            }
            foreach (var id in createdCipherIds)
            {
                try { await writeService.DeleteCipherAsync(id, permanent: true, cleanup.Token); }
                catch { cleanupOk = false; }
            }
            foreach (var id in createdFolderIds)
            {
                try { await writeService.DeleteFolderAsync(id, cleanup.Token); }
                catch { cleanupOk = false; }
            }
            foreach (var source in sources)
                CryptographicOperations.ZeroMemory(source.Bytes);
            step("formats: owned items and folders cleaned", cleanupOk
                && session.Ciphers.All(value => !createdCipherIds.Contains(value.Id))
                && session.Folders.All(value => !createdFolderIds.Contains(value.Id)),
                $"owned items={createdCipherIds.Count}; owned folders={createdFolderIds.Count}");
        }

        void CaptureOwned(IReadOnlyList<Cipher> ciphers)
        {
            foreach (var cipher in ciphers)
            {
                if (!originalCipherIds.Contains(cipher.Id)
                    && sources.Any(source => cipher.Name == source.Name
                        || cipher.Login?.Username == source.Username))
                    createdCipherIds.Add(cipher.Id);
            }
            foreach (var folder in session.Folders)
                if (!originalFolderIds.Contains(folder.Id)
                    && folder.Name.StartsWith(prefix + "-", StringComparison.Ordinal))
                    createdFolderIds.Add(folder.Id);
        }
    }

    private static Source[] BuildSources(string prefix)
    {
        const string password = "  synthetic secret,\"quoted\"\r\nsecond line  ";
        Source CsvSource(ImportFormat format, string label, string[] header, string[] row,
            string? name = null, string? folder = null) => new(
                format, label, Encoding.UTF8.GetBytes(Csv(header, row)), name ?? prefix + "-" + label,
                prefix + "-" + label + "-user", password, folder);

        var csvFolder = prefix + "-bitwarden-folder";
        var lastPassFolder = prefix + "-lastpass-folder";
        var keePassFolder = prefix + "-keepass-folder";
        var onePasswordFolder = prefix + "-onepassword-folder";
        var firefoxHost = "wvw-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prefix)))[..16]
            .ToLowerInvariant() + ".example.test";
        var keePassName = prefix + "-keepass";
        var keePassUser = prefix + "-keepass-user";
        var keePassPassword = password.Replace("\r\n", "\n", StringComparison.Ordinal);
        var keepassXml = new XDocument(new XElement("KeePassFile", new XElement("Root",
            new XElement("Group", new XElement("Name", "Synthetic database"),
                new XElement("Group", new XElement("Name", keePassFolder),
                    new XElement("Entry", Field("Title", keePassName), Field("UserName", keePassUser),
                        Field("Password", keePassPassword), Field("URL", "https://keepass.example.test/")))))))
            .ToString(SaveOptions.DisableFormatting);

        return
        [
            CsvSource(ImportFormat.Csv, "bitwarden",
                ["folder", "favorite", "type", "name", "notes", "fields", "reprompt", "login_uri", "login_username", "login_password", "login_totp"],
                [csvFolder, "0", "login", prefix + "-bitwarden", "Synthetic notes", "", "0", "https://csv.example.test/", prefix + "-bitwarden-user", password, ""],
                folder: csvFolder),
            CsvSource(ImportFormat.ChromeCsv, "chrome", ["name", "url", "username", "password", "note"],
                [prefix + "-chrome", "https://chrome.example.test/", prefix + "-chrome-user", password, "Synthetic notes"]),
            CsvSource(ImportFormat.EdgeCsv, "edge", ["name", "url", "username", "password"],
                [prefix + "-edge", "https://edge.example.test/", prefix + "-edge-user", password]),
            CsvSource(ImportFormat.FirefoxCsv, "firefox", ["url", "username", "password", "httpRealm", "formActionOrigin", "guid", "timeCreated", "timeLastUsed", "timePasswordChanged"],
                ["https://" + firefoxHost + "/login", prefix + "-firefox-user", password, "Synthetic realm", "", "{synthetic-guid}", "1700000000000", "1700000100000", "1700000050000"],
                name: firefoxHost),
            CsvSource(ImportFormat.LastPassCsv, "lastpass", ["url", "username", "password", "extra", "name", "grouping", "fav"],
                ["https://lastpass.example.test/", prefix + "-lastpass-user", password, "Synthetic notes", prefix + "-lastpass", lastPassFolder, "1"],
                folder: lastPassFolder),
            new Source(ImportFormat.KeePassXml, "keepass", Encoding.UTF8.GetBytes(keepassXml),
                keePassName, keePassUser, keePassPassword, keePassFolder),
            new Source(ImportFormat.OnePasswordArchive, "onepassword",
                OnePasswordArchive(prefix + "-onepassword", prefix + "-onepassword-user", password, onePasswordFolder),
                prefix + "-onepassword", prefix + "-onepassword-user", password, onePasswordFolder),
        ];
    }

    private static XElement Field(string name, string value) => new("String",
        new XElement("Key", name), new XElement("Value", value));

    private static string Csv(params string[][] rows) => string.Join("\r\n",
        rows.Select(row => string.Join(",", row.Select(value => "\"" + value.Replace("\"", "\"\"") + "\"")))) + "\r\n";

    private static byte[] OnePasswordArchive(string name, string username, string password, string folder)
    {
        JsonObject LoginField(string designation, string value) => new()
        {
            ["designation"] = designation, ["value"] = value,
            ["name"] = designation, ["fieldType"] = designation == "password" ? "P" : "T",
        };
        var item = new JsonObject
        {
            ["uuid"] = "synthetic-item", ["favIndex"] = 0,
            ["createdAt"] = 1_600_000_000, ["updatedAt"] = 1_600_000_010,
            ["state"] = "active", ["categoryUuid"] = "001",
            ["overview"] = new JsonObject
            {
                ["title"] = name, ["url"] = "https://onepassword.example.test/",
                ["urls"] = new JsonArray(), ["tags"] = new JsonArray(),
            },
            ["details"] = new JsonObject
            {
                ["notesPlain"] = "Synthetic notes",
                ["loginFields"] = new JsonArray(LoginField("username", username), LoginField("password", password)),
                ["sections"] = new JsonArray(), ["passwordHistory"] = new JsonArray(),
            },
        };
        var data = new JsonObject
        {
            ["accounts"] = new JsonArray(new JsonObject
            {
                ["attrs"] = new JsonObject
                {
                    ["uuid"] = "synthetic-account", ["accountName"] = "Synthetic account",
                    ["name"] = "Synthetic account", ["email"] = "synthetic@example.test",
                    ["domain"] = "https://example.1password.com/",
                },
                ["vaults"] = new JsonArray(new JsonObject
                {
                    ["attrs"] = new JsonObject
                    {
                        ["uuid"] = "synthetic-vault", ["name"] = folder, ["desc"] = "Synthetic vault", ["type"] = "P",
                    },
                    ["items"] = new JsonArray(item),
                }),
            }),
        };
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write("export.attributes", "{\"version\":3,\"description\":\"1Password Unencrypted Export\",\"createdAt\":1600000000}");
            Write("export.data", data.ToJsonString());
            void Write(string entryName, string content)
            {
                using var output = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                try { output.Write(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
        return memory.ToArray();
    }

    private sealed record Source(
        ImportFormat Format,
        string Label,
        byte[] Bytes,
        string Name,
        string Username,
        string Password,
        string? FolderName);
}
