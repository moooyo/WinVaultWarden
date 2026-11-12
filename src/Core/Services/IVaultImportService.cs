namespace Core.Services;

// 支持的导入文件格式。
public enum ImportFormat
{
    Json,
    Csv,
    OnePasswordArchive,
    KeePassXml,
    ChromeCsv,
    EdgeCsv,
    FirefoxCsv,
    LastPassCsv,
}

/// <summary>A null destination preserves source folders; a non-null ID selects an existing personal folder.</summary>
public sealed record VaultImportOptions(string? DestinationFolderId = null);

// 导入预览:解析成功给出条目/文件夹计数;解析失败 Error 非空、其余为 0。
public sealed record ImportPreview(int Ciphers, int Folders, string? Error);

// 导入编排:解析导出文件 → 加密 → 批量导入 → 整库重新同步。
// ImportAsync 要求 vault 处于解锁态,否则抛 InvalidOperationException。
public interface IVaultImportService
{
    // 仅解析,不加密、不发请求。解析失败时返回 Error 非空的预览,不抛异常。
    ImportPreview Parse(ImportFormat format, string content);

    ImportPreview Parse(ImportFormat format, string content, VaultImportOptions options) => Parse(format, content);

    // 解析 → 加密 → 调用 /ciphers/import → 整库重新同步。返回导入的条目数。
    // vault 处于锁定态时抛 InvalidOperationException;文件内容解析失败时异常向上抛出。
    Task<int> ImportAsync(ImportFormat format, string content, CancellationToken ct = default);

    Task<int> ImportAsync(
        ImportFormat format,
        string content,
        VaultImportOptions options,
        CancellationToken ct = default)
    {
        if (options.DestinationFolderId is not null)
            throw new NotSupportedException("This import provider does not support a destination folder.");
        return ImportAsync(format, content, ct);
    }

    /// <summary>Returns only folders belonging to the currently unlocked personal vault.</summary>
    IReadOnlyList<Core.Models.Folder> GetDestinationFolders() => [];

    /// <summary>Decodes a bounded source file to canonical plaintext JSON, entirely in memory.</summary>
    Task<string> PrepareFileAsync(
        ImportFormat format,
        ReadOnlyMemory<byte> bytes,
        CancellationToken ct = default) =>
        Task.FromException<string>(new NotSupportedException("This import provider cannot decode binary source files."));

    /// <summary>Identifies the protection of a JSON backup without decrypting its contents.</summary>
    ImportProtection InspectJsonProtection(string content) => ImportProtection.None;

    /// <summary>Returns plaintext JSON in memory after validating and decrypting a backup.</summary>
    Task<string> UnlockJsonAsync(
        string content,
        string? password,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(content);
    }
}
