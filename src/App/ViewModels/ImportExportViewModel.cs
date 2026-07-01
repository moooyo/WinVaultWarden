using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Services;

namespace App.ViewModels;

// 导入/导出页 VM：导出侧只产出明文字符串交给 code-behind 落盘；导入侧解析预览 + 实际导入。
// 不接触文件 I/O、picker、ContentDialog —— 这些留在 code-behind，保证本类可脱离 WinUI 单元测试。
public partial class ImportExportViewModel : ObservableObject
{
    private readonly IVaultExportService _export;
    private readonly IVaultImportService _import;
    private readonly IAccountService _account;

    public ImportExportViewModel(IVaultExportService export, IVaultImportService import, IAccountService account)
    {
        _export = export;
        _import = import;
        _account = account;
    }

    // ── 导出 ─────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportFormatIndex))]
    public partial ExportFormat SelectedExportFormat { get; set; } = ExportFormat.Json;

    /// <summary>供 ComboBox.SelectedIndex 双向绑定使用（0=Json，1=Csv）。</summary>
    public int ExportFormatIndex
    {
        get => (int)SelectedExportFormat;
        set => SelectedExportFormat = (ExportFormat)value;
    }

    public string ExportToText() => _export.Export(SelectedExportFormat);

    public Task<bool> VerifyMasterPasswordAsync(string password) => _account.VerifyMasterPasswordAsync(password);

    // ── 导入 ─────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportFormatIndex))]
    public partial ImportFormat SelectedImportFormat { get; set; } = ImportFormat.Json;

    /// <summary>供 ComboBox.SelectedIndex 双向绑定使用（0=Json，1=Csv）。</summary>
    public int ImportFormatIndex
    {
        get => (int)SelectedImportFormat;
        set => SelectedImportFormat = (ImportFormat)value;
    }

    [ObservableProperty]
    public partial string ImportContent { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int PreviewCipherCount { get; set; }

    [ObservableProperty]
    public partial int PreviewFolderCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportError))]
    public partial string? ImportError { get; set; }

    public bool HasImportError => !string.IsNullOrEmpty(ImportError);

    [ObservableProperty]
    public partial string? ResultMessage { get; set; }

    [RelayCommand]
    private void PreviewImport()
    {
        var preview = _import.Parse(SelectedImportFormat, ImportContent);
        PreviewCipherCount = preview.Ciphers;
        PreviewFolderCount = preview.Folders;
        ImportError = preview.Error;
    }

    [RelayCommand]
    private async Task DoImportAsync()
    {
        if (HasImportError || string.IsNullOrEmpty(ImportContent))
            return;

        try
        {
            var count = await _import.ImportAsync(SelectedImportFormat, ImportContent);
            ResultMessage = $"已导入 {count} 个条目";
        }
        catch (Exception ex)
        {
            ResultMessage = $"导入失败: {ex.Message}";
        }
    }
}
