namespace Core.Services;

// 支持的导入文件格式。
public enum ImportFormat
{
    Json,
    Csv,
}

// 导入预览:解析成功给出条目/文件夹计数;解析失败 Error 非空、其余为 0。
public sealed record ImportPreview(int Ciphers, int Folders, string? Error);

// 导入编排:解析导出文件 → 加密 → 批量导入 → 整库重新同步。
// ImportAsync 要求 vault 处于解锁态,否则抛 InvalidOperationException。
public interface IVaultImportService
{
    // 仅解析,不加密、不发请求。解析失败时返回 Error 非空的预览,不抛异常。
    ImportPreview Parse(ImportFormat format, string content);

    // 解析 → 加密 → 调用 /ciphers/import → 整库重新同步。返回导入的条目数。
    // vault 处于锁定态时抛 InvalidOperationException;文件内容解析失败时异常向上抛出。
    Task<int> ImportAsync(ImportFormat format, string content, CancellationToken ct = default);
}
