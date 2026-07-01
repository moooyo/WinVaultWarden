namespace Core.Services;

public enum ExportFormat
{
    Json,
    Csv,
}

public interface IVaultExportService
{
    string Export(ExportFormat format);
}
