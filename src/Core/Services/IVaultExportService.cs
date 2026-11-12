namespace Core.Services;

public enum ExportFormat
{
    Json,
    Csv,
    PasswordProtectedJson,
    AccountRestrictedJson,
}

public interface IVaultExportService
{
    bool CanExport => true;
    string Export(ExportFormat format);

    /// <summary>Produces an explicitly selected encrypted backup without writing plaintext to disk.</summary>
    Task<string> ExportProtectedAsync(
        ExportFormat format,
        string? password,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (format is not (ExportFormat.Json or ExportFormat.Csv))
            throw new NotSupportedException("Encrypted export is unavailable for this vault provider.");
        return Task.FromResult(Export(format));
    }
}
