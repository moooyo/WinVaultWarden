namespace Core.Services;

public enum ExportFormat
{
    Json,
    Csv,
}

public interface IVaultExportService
{
    bool CanExport => true;
    string Export(ExportFormat format);
}
