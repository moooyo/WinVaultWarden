using System.Globalization;
using System.Text;
using Core.Enums;
using Core.Models;

namespace Vault.Porting;

// 域模型 <-> Bitwarden 未加密 CSV 导出格式的编解码器（面向登录，RFC4180 转义）。
// 纯函数：不涉及网络、加密；调用方负责在导出前解密、导入后加密。
//
// 注意：CSV 格式只有 11 个固定列，非 login 类型（Card/Identity/SecureNote/SshKey）的
// 类型专属字段（卡号、身份信息、SecureNote.Type、SSH 私钥等）不会被导出，属于
// 有意为之的有损转换——需要无损往返请使用 BitwardenJsonCodec（Task 1）。
public static class BitwardenCsvCodec
{
    private static readonly string[] ColumnNames =
    [
        "folder", "favorite", "type", "name", "notes", "fields",
        "reprompt", "login_uri", "login_username", "login_password", "login_totp",
    ];

    public static string Serialize(IReadOnlyList<Cipher> ciphers, IReadOnlyList<Folder> folders)
    {
        var folderNameById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in folders)
        {
            if (!string.IsNullOrEmpty(f.Id))
                folderNameById[f.Id] = f.Name;
        }

        var rows = new List<string[]> { ColumnNames };

        foreach (var c in ciphers)
        {
            string folderName = string.Empty;
            if (!string.IsNullOrEmpty(c.FolderId) && folderNameById.TryGetValue(c.FolderId, out var name))
                folderName = name;

            var fields = c.Fields.Count == 0
                ? string.Empty
                : string.Join("\n", c.Fields.Select(f => $"{f.Name}: {f.Value}"));

            string loginUri = string.Empty;
            string loginUsername = string.Empty;
            string loginPassword = string.Empty;
            string loginTotp = string.Empty;
            if (c.Type == CipherType.Login && c.Login is not null)
            {
                loginUri = c.Login.Uris.Count > 0 ? c.Login.Uris[0].Uri ?? string.Empty : string.Empty;
                loginUsername = c.Login.Username ?? string.Empty;
                loginPassword = c.Login.Password ?? string.Empty;
                loginTotp = c.Login.Totp ?? string.Empty;
            }

            rows.Add(new[]
            {
                folderName,
                c.Favorite ? "1" : string.Empty,
                TypeToWord(c.Type),
                c.Name,
                c.Notes ?? string.Empty,
                fields,
                c.Reprompt ? "1" : "0",
                loginUri,
                loginUsername,
                loginPassword,
                loginTotp,
            });
        }

        return WriteCsv(rows);
    }

    public static PortingData Parse(string csv)
    {
        var rows = ParseCsv(csv);
        if (rows.Count == 0)
            return new PortingData(Array.Empty<Cipher>(), Array.Empty<Folder>(), Array.Empty<(int, int)>());

        // 首行是表头，跳过。
        var ciphers = new List<Cipher>(rows.Count - 1);
        var folders = new List<Folder>();
        var folderIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var relations = new List<(int CipherIndex, int FolderIndex)>();

        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var folderName = Cell(row, 0);
            var favorite = Cell(row, 1) == "1";
            var type = WordToType(Cell(row, 2));
            var name = Cell(row, 3);
            var notes = Cell(row, 4);
            var fieldsCell = Cell(row, 5);
            var reprompt = Cell(row, 6) == "1";
            var loginUri = Cell(row, 7);
            var loginUsername = Cell(row, 8);
            var loginPassword = Cell(row, 9);
            var loginTotp = Cell(row, 10);

            var cipherIndex = ciphers.Count;

            string? folderId = null;
            if (!string.IsNullOrEmpty(folderName))
            {
                if (!folderIndexByName.TryGetValue(folderName, out var folderIdx))
                {
                    folderIdx = folders.Count;
                    folderIndexByName[folderName] = folderIdx;
                    folders.Add(new Folder { Id = folderIdx.ToString(CultureInfo.InvariantCulture), Name = folderName });
                }

                folderId = folderIdx.ToString(CultureInfo.InvariantCulture);
                relations.Add((cipherIndex, folderIdx));
            }

            CipherLogin? login = null;
            if (type == CipherType.Login)
            {
                login = new CipherLogin(
                    string.IsNullOrEmpty(loginUsername) ? null : loginUsername,
                    string.IsNullOrEmpty(loginPassword) ? null : loginPassword,
                    string.IsNullOrEmpty(loginTotp) ? null : loginTotp,
                    string.IsNullOrEmpty(loginUri) ? Array.Empty<CipherLoginUri>() : new[] { new CipherLoginUri(loginUri, null) });
            }

            ciphers.Add(new Cipher
            {
                Id = string.Empty,
                Type = type,
                FolderId = folderId,
                Favorite = favorite,
                Reprompt = reprompt,
                Name = name,
                Notes = string.IsNullOrEmpty(notes) ? null : notes,
                Login = login,
                Fields = ParseFields(fieldsCell),
            });
        }

        return new PortingData(ciphers, folders, relations);
    }

    private static string Cell(string[] row, int index) => index < row.Length ? row[index] : string.Empty;

    private static IReadOnlyList<CipherField> ParseFields(string cell)
    {
        if (string.IsNullOrEmpty(cell))
            return Array.Empty<CipherField>();

        var lines = cell.Split('\n');
        var result = new List<CipherField>(lines.Length);
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;

            var sepIndex = line.IndexOf(": ", StringComparison.Ordinal);
            if (sepIndex < 0)
            {
                result.Add(new CipherField(line, null, CipherFieldType.Text));
            }
            else
            {
                var fieldName = line[..sepIndex];
                var value = line[(sepIndex + 2)..];
                result.Add(new CipherField(fieldName, value, CipherFieldType.Text));
            }
        }

        return result;
    }

    private static string TypeToWord(CipherType type) => type switch
    {
        CipherType.Login => "login",
        CipherType.SecureNote => "note",
        CipherType.Card => "card",
        CipherType.Identity => "identity",
        CipherType.SshKey => "sshkey",
        _ => "login",
    };

    private static CipherType WordToType(string word) => word switch
    {
        "login" => CipherType.Login,
        "note" => CipherType.SecureNote,
        "card" => CipherType.Card,
        "identity" => CipherType.Identity,
        "sshkey" => CipherType.SshKey,
        _ => CipherType.Login, // Bitwarden CSV 以 login 为中心格式，未知/空类型回退为 login。
    };

    // ---- RFC4180 读写（无第三方依赖，纯字符串状态机，AOT 友好） ----

    private static string WriteCsv(IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var row in rows)
        {
            if (!first)
                sb.Append('\n');
            first = false;

            for (var i = 0; i < row.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(Field(row[i]));
            }
        }

        return sb.ToString();
    }

    private static string Field(string? value)
    {
        value ??= string.Empty;
        var needsQuoting = value.IndexOfAny(['"', ',', '\r', '\n']) >= 0;
        if (!needsQuoting)
            return value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static List<string[]> ParseCsv(string csv)
    {
        var rows = new List<string[]>();
        if (string.IsNullOrEmpty(csv))
            return rows;

        var currentRow = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var rowHasContent = false;
        var i = 0;
        var length = csv.Length;

        void EndField()
        {
            currentRow.Add(field.ToString());
            field.Clear();
        }

        void EndRow()
        {
            EndField();
            rows.Add(currentRow.ToArray());
            currentRow = new List<string>();
            rowHasContent = false;
        }

        while (i < length)
        {
            var ch = csv[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                field.Append(ch);
                i++;
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    rowHasContent = true;
                    i++;
                    break;
                case ',':
                    rowHasContent = true;
                    EndField();
                    i++;
                    break;
                case '\r':
                    rowHasContent = true;
                    EndRow();
                    i++;
                    if (i < length && csv[i] == '\n')
                        i++;
                    break;
                case '\n':
                    rowHasContent = true;
                    EndRow();
                    i++;
                    break;
                default:
                    rowHasContent = true;
                    field.Append(ch);
                    i++;
                    break;
            }
        }

        // 结尾没有换行符时，仍需把最后一行/字段收尾；但若结尾恰好是换行符，
        // 上面的 EndRow() 已经处理完毕，不应再补一条空行。
        if (rowHasContent || field.Length > 0 || currentRow.Count > 0)
            EndRow();

        return rows;
    }
}
