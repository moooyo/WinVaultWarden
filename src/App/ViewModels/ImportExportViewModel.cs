using Api;
using App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Services;

namespace App.ViewModels;

/// <summary>预览快照：内容/格式/版本在拍摄时冻结，之后不可变更。</summary>
public sealed record ImportPreviewSnapshot(
    ImportFormat Format,
    long ContentVersion,
    string Content,
    ImportPreview Preview);

// 导入/导出页 VM：导出侧只产出明文字符串交给 code-behind 落盘；导入侧解析预览 + 实际导入。
// 不接触文件 I/O、picker、ContentDialog —— 这些留在 code-behind，保证本类可脱离 WinUI 单元测试。
public partial class ImportExportViewModel : ObservableObject, ISensitiveState
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

    [ObservableProperty]
    public partial string? ExportStatus { get; set; }

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

    // ── 快照与版本控制 ────────────────────────────────────────────────────────

    private long _contentVersion;
    private ImportPreviewSnapshot? _previewSnapshot;

    /// <summary>当前预览是否有效：快照不为空、格式与版本匹配且无解析错误。</summary>
    public bool HasValidPreview =>
        _previewSnapshot is { } snapshot
        && snapshot.Format == SelectedImportFormat
        && snapshot.ContentVersion == _contentVersion
        && snapshot.Preview.Error is null;

    /// <summary>当且仅当有效预览存在且内容不为空时才允许导入。</summary>
    public bool CanImport => HasValidPreview && !string.IsNullOrEmpty(ImportContent);

    partial void OnImportContentChanged(string value)
    {
        checked { _contentVersion++; }
        ClearPreview();
    }

    partial void OnSelectedImportFormatChanged(ImportFormat value)
    {
        OnPropertyChanged(nameof(ImportFormatIndex));
        ClearPreview();
    }

    /// <summary>清空预览快照、计数和错误。格式/内容变更时自动调用。</summary>
    public void ClearPreview()
    {
        _previewSnapshot = null;
        PreviewCipherCount = 0;
        PreviewFolderCount = 0;
        ImportError = null;
        OnPropertyChanged(nameof(HasValidPreview));
        OnPropertyChanged(nameof(CanImport));
    }

    private void ClearSnapshotKeepError()
    {
        _previewSnapshot = null;
        PreviewCipherCount = 0;
        PreviewFolderCount = 0;
        OnPropertyChanged(nameof(HasValidPreview));
        OnPropertyChanged(nameof(CanImport));
    }

    // ── 预览：冻结当前格式+版本+内容到不可变快照 ─────────────────────────────

    [RelayCommand]
    private void PreviewImport()
    {
        var content = ImportContent;
        var format = SelectedImportFormat;
        var version = _contentVersion;
        var preview = _import.Parse(format, content);

        _previewSnapshot = new ImportPreviewSnapshot(format, version, content, preview);
        PreviewCipherCount = preview.Ciphers;
        PreviewFolderCount = preview.Folders;
        ImportError = preview.Error;
        OnPropertyChanged(nameof(HasValidPreview));
        OnPropertyChanged(nameof(CanImport));
    }

    // ── 导入：对快照重解析以防漂移，再调用 ImportAsync ───────────────────────

    [RelayCommand]
    public async Task<UiOperationResult<int>> DoImportAsync(CancellationToken ct = default)
    {
        var snapshot = _previewSnapshot;
        if (snapshot is null
            || snapshot.Format != SelectedImportFormat
            || snapshot.ContentVersion != _contentVersion
            || snapshot.Preview.Error is not null)
        {
            return UiOperationResult<int>.Failure("请先为当前文件生成预览");
        }

        // 重解析防漂移：确认 Parse 结果与拍摄时完全一致
        var reparsed = _import.Parse(snapshot.Format, snapshot.Content);
        if (reparsed != snapshot.Preview)
        {
            ImportError = "导入预览已变化，请重新预览后再导入";
            ClearSnapshotKeepError();
            return UiOperationResult<int>.Failure(ImportError);
        }

        try
        {
            var count = await _import.ImportAsync(snapshot.Format, snapshot.Content, ct);
            ResultMessage = $"已导入 {count} 个条目";
            return UiOperationResult<int>.Success(count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return UiOperationResult<int>.Cancelled();
        }
        catch (Exception ex) when (ex is InvalidDataException
            or FormatException
            or InvalidOperationException
            or VaultWriteException
            or HttpRequestException)
        {
            ResultMessage = "导入失败，请确认文件仍有效且密码库已解锁。";
            return UiOperationResult<int>.Failure(ResultMessage);
        }
    }

    // 锁屏/登出时清空导入/导出的机密足迹：导入内容（可能含明文条目）、快照、预览计数与状态消息。幂等。
    public void ClearSensitive()
    {
        ImportContent = string.Empty;
        _previewSnapshot = null;
        PreviewCipherCount = 0;
        PreviewFolderCount = 0;
        ImportError = null;
        ResultMessage = null;
        ExportStatus = null;
        OnPropertyChanged(nameof(HasValidPreview));
        OnPropertyChanged(nameof(CanImport));
    }
}
