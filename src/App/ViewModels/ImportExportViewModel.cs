using Api;
using App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Services;

namespace App.ViewModels;

public enum ImportExportOperationKind
{
    ChooseImportFile,
    PreviewImport,
    Import,
    Export,
}

public sealed record ImportPreviewSnapshot(
    ImportFormat Format,
    long ContentVersion,
    string Content,
    ImportPreview Preview);

/// <summary>
/// Owns one import/export operation and the formats selected when it began. A scope that is
/// disposed without an explicit terminal result is treated as cancelled.
/// </summary>
public sealed class ImportExportOperationScope : IDisposable
{
    private ImportExportViewModel? _owner;
    private IDisposable? _gateLease;
    private readonly CancellationTokenSource _cancellation = new();
    private int _completionClaimed;

    internal ImportExportOperationScope(
        ImportExportViewModel owner,
        IDisposable gateLease,
        ImportExportOperationKind kind,
        ExportFormat exportFormat,
        ImportFormat importFormat,
        long generation)
    {
        _owner = owner;
        _gateLease = gateLease;
        Kind = kind;
        ExportFormat = exportFormat;
        ImportFormat = importFormat;
        Generation = generation;
    }

    public ImportExportOperationKind Kind { get; }
    public ExportFormat ExportFormat { get; }
    public ImportFormat ImportFormat { get; }
    public CancellationToken Token => _cancellation.Token;
    internal long Generation { get; }

    public void Complete(
        UiOperationStatus status,
        string? message = null,
        bool isWarning = false)
    {
        if (status == UiOperationStatus.Busy)
            throw new ArgumentOutOfRangeException(nameof(status));
        if (Interlocked.CompareExchange(ref _completionClaimed, 1, 0) != 0)
            return;

        var owner = _owner;
        var gateLease = _gateLease;
        _owner = null;
        _gateLease = null;
        try
        {
            if (owner is null || gateLease is null)
            {
                gateLease?.Dispose();
                return;
            }

            owner.EndOperation(this, gateLease, status, message, isWarning);
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    public void Dispose() => Complete(UiOperationStatus.Cancelled);

    internal void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException)
        {
            // Lifecycle cleanup must still invalidate this operation if a callback misbehaves.
        }
    }
}

public partial class ImportExportViewModel : ObservableObject, ISensitiveState
{
    private const string ImportParseFailureMessage = "无法解析导入文件，请确认文件格式和内容正确。";
    private const string UnsupportedImportFileMessage = "请选择 JSON 或 CSV 格式的导入文件。";
    private const string MissingPreviewMessage = "请先为当前文件生成有效预览。";
    private const string PreviewChangedMessage = "导入预览已变化，请重新预览后再导入。";
    private const string ImportFailureMessage = "导入失败，请确认文件仍有效且密码库已解锁。";
    private const string ExportPolicyMessage = "组织策略已禁用个人密码库导出。";

    private readonly IVaultExportService _export;
    private readonly IVaultImportService _import;
    private readonly IAccountService _account;
    private readonly AsyncOperationGate _operations = new();
    private ImportExportOperationScope? _activeOperation;
    private long _operationGeneration;
    private long _contentVersion;
    private ImportPreviewSnapshot? _previewSnapshot;

    public ImportExportViewModel(
        IVaultExportService export,
        IVaultImportService import,
        IAccountService account)
    {
        _export = export;
        _import = import;
        _account = account;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportFormatIndex))]
    public partial ExportFormat SelectedExportFormat { get; set; } = ExportFormat.Json;

    public int ExportFormatIndex
    {
        get => (int)SelectedExportFormat;
        set
        {
            if (Enum.IsDefined((ExportFormat)value))
                SelectedExportFormat = (ExportFormat)value;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportFormatIndex))]
    public partial ImportFormat SelectedImportFormat { get; set; } = ImportFormat.Json;

    public int ImportFormatIndex
    {
        get => (int)SelectedImportFormat;
        set
        {
            if (Enum.IsDefined((ImportFormat)value))
                SelectedImportFormat = (ImportFormat)value;
        }
    }

    [ObservableProperty]
    public partial string ImportContent { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportFileLabel))]
    public partial string? SelectedImportFileName { get; private set; }

    public string ImportFileLabel => SelectedImportFileName ?? "尚未选择文件";
    public bool CanPreviewAgain => CanOperate && !string.IsNullOrWhiteSpace(ImportContent) && !HasValidPreview;
    public string ExportFormatDescription => SelectedExportFormat == ExportFormat.Json
        ? "JSON 保留受支持的条目字段。"
        : "CSV 仅适用于登录和安全笔记；其他类型请使用 JSON。";

    [ObservableProperty]
    public partial int PreviewCipherCount { get; set; }

    [ObservableProperty]
    public partial int PreviewFolderCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyMessage))]
    public partial ImportExportOperationKind? CurrentOperation { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(CanPreviewAgain))]
    [NotifyPropertyChangedFor(nameof(HasSucceededStatus))]
    [NotifyPropertyChangedFor(nameof(HasWarningStatus))]
    [NotifyPropertyChangedFor(nameof(HasCancelledStatus))]
    [NotifyPropertyChangedFor(nameof(HasFailedStatus))]
    public partial UiOperationStatus? OperationStatus { get; private set; }

    [ObservableProperty]
    public partial string? OperationMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSucceededStatus))]
    [NotifyPropertyChangedFor(nameof(HasWarningStatus))]
    public partial bool OperationHasWarning { get; private set; }

    public bool IsBusy => OperationStatus == UiOperationStatus.Busy;
    public bool CanOperate => !IsBusy;
    public bool CanExport => !IsBusy && _export.CanExport;
    public bool ShowExportPolicyInfo => !_export.CanExport;
    public string ExportPolicyNotice => ExportPolicyMessage;
    public bool HasSucceededStatus =>
        OperationStatus == UiOperationStatus.Succeeded && !OperationHasWarning;
    public bool HasWarningStatus =>
        OperationStatus == UiOperationStatus.Succeeded && OperationHasWarning;
    public bool HasCancelledStatus => OperationStatus == UiOperationStatus.Cancelled;
    public bool HasFailedStatus => OperationStatus == UiOperationStatus.Failed;

    public string BusyMessage => CurrentOperation switch
    {
        ImportExportOperationKind.ChooseImportFile => "正在读取并解析导入文件…",
        ImportExportOperationKind.PreviewImport => "正在解析导入内容…",
        ImportExportOperationKind.Import => "正在导入密码库…",
        ImportExportOperationKind.Export => "正在导出密码库…",
        _ => string.Empty,
    };

    public bool HasValidPreview =>
        _previewSnapshot is { } snapshot
        && snapshot.Format == SelectedImportFormat
        && snapshot.ContentVersion == _contentVersion
        && snapshot.Preview.Error is null;

    public bool CanImport =>
        !IsBusy
        && HasValidPreview
        && PreviewCipherCount + PreviewFolderCount > 0
        && !string.IsNullOrEmpty(ImportContent);

    public UiOperationResult<ImportExportOperationScope> BeginOperation(
        ImportExportOperationKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));

        if (kind == ImportExportOperationKind.Export && !_export.CanExport)
            return UiOperationResult<ImportExportOperationScope>.Failure(ExportPolicyMessage);

        if (!_operations.TryEnter(out var gateLease) || gateLease is null)
            return UiOperationResult<ImportExportOperationScope>.Busy();

        try
        {
            ClearOperationMessages();
            if (kind == ImportExportOperationKind.ChooseImportFile)
            {
                ImportContent = string.Empty;
                SelectedImportFileName = null;
                ClearPreview();
            }
            else if (kind == ImportExportOperationKind.PreviewImport)
            {
                ClearPreview();
            }

            var operation = new ImportExportOperationScope(
                this,
                gateLease,
                kind,
                SelectedExportFormat,
                SelectedImportFormat,
                Interlocked.Increment(ref _operationGeneration));
            _activeOperation = operation;
            CurrentOperation = kind;
            OperationStatus = UiOperationStatus.Busy;
            return UiOperationResult<ImportExportOperationScope>.Success(operation);
        }
        catch
        {
            gateLease.Dispose();
            throw;
        }
    }

    internal void EndOperation(
        ImportExportOperationScope operation,
        IDisposable gateLease,
        UiOperationStatus status,
        string? message,
        bool isWarning)
    {
        try
        {
            if (!ReferenceEquals(_activeOperation, operation))
                return;

            _activeOperation = null;
            if (operation.Generation != Volatile.Read(ref _operationGeneration))
            {
                CurrentOperation = null;
                OperationMessage = null;
                OperationHasWarning = false;
                OperationStatus = null;
                return;
            }

            var resolvedMessage = message ?? DefaultMessage(status);
            CurrentOperation = null;
            OperationMessage = resolvedMessage;
            OperationHasWarning = isWarning;
            OperationStatus = status;
        }
        finally
        {
            gateLease.Dispose();
        }
    }

    public string ExportToText(ExportFormat format) => _export.Export(format);

    public Task<bool> VerifyMasterPasswordAsync(
        string password,
        CancellationToken ct = default) =>
        _account.VerifyMasterPasswordAsync(password, ct);

    public static bool TryGetImportFormat(string? fileName, out ImportFormat format)
    {
        format = default;
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        string extension;
        try
        {
            extension = Path.GetExtension(fileName);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            format = ImportFormat.Json;
            return true;
        }

        if (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            format = ImportFormat.Csv;
            return true;
        }

        return false;
    }

    public UiOperationResult ApplySelectedImportFile(
        ImportExportOperationScope operation,
        string fileName,
        string content)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(content);

        if (!IsActive(operation, ImportExportOperationKind.ChooseImportFile))
            return UiOperationResult.Cancelled();

        if (!TryGetImportFormat(fileName, out var format))
        {
            operation.Complete(UiOperationStatus.Failed, UnsupportedImportFileMessage);
            return UiOperationResult.Failure(UnsupportedImportFileMessage);
        }

        SelectedImportFormat = format;
        ImportContent = content;
        SelectedImportFileName = Path.GetFileName(fileName);
        return ParsePreview(operation, format, _contentVersion, content);
    }

    [RelayCommand]
    private void PreviewImport() => _ = PreviewCurrentImport();

    public UiOperationResult PreviewCurrentImport()
    {
        var started = BeginOperation(ImportExportOperationKind.PreviewImport);
        if (!started.IsSuccess || started.Value is not { } operation)
            return UiOperationResult.Busy();

        using (operation)
        {
            return ParsePreview(
                operation,
                operation.ImportFormat,
                _contentVersion,
                ImportContent);
        }
    }

    [RelayCommand]
    public async Task<UiOperationResult<int>> DoImportAsync(CancellationToken ct = default)
    {
        var started = BeginOperation(ImportExportOperationKind.Import);
        if (!started.IsSuccess || started.Value is not { } operation)
            return UiOperationResult<int>.Busy();

        using (operation)
        {
            var snapshot = _previewSnapshot;
            if (snapshot is null
                || snapshot.Format != operation.ImportFormat
                || snapshot.Format != SelectedImportFormat
                || snapshot.ContentVersion != _contentVersion
                || snapshot.Preview.Error is not null
                || snapshot.Preview.Ciphers + snapshot.Preview.Folders == 0)
            {
                operation.Complete(UiOperationStatus.Failed, MissingPreviewMessage);
                return UiOperationResult<int>.Failure(MissingPreviewMessage);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                operation.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                var reparsed = _import.Parse(snapshot.Format, snapshot.Content);
                if (reparsed != snapshot.Preview)
                {
                    ClearPreview();
                    operation.Complete(UiOperationStatus.Failed, PreviewChangedMessage);
                    return UiOperationResult<int>.Failure(PreviewChangedMessage);
                }

                var count = await _import.ImportAsync(
                    snapshot.Format,
                    snapshot.Content,
                    linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                ConsumePreview(snapshot);
                var message = $"已导入 {count} 个条目。";
                operation.Complete(UiOperationStatus.Succeeded, message);
                return UiOperationResult<int>.Success(count);
            }
            catch (OperationCanceledException ex)
                when (UiExceptionPolicy.IsGenuineCancellation(ex, linked.Token))
            {
                operation.Complete(UiOperationStatus.Cancelled);
                return UiOperationResult<int>.Cancelled();
            }
            catch (VaultRefreshException)
            {
                ConsumePreview(snapshot);
                const string message = "导入已完成，但本地数据刷新失败，请手动同步。";
                operation.Complete(
                    UiOperationStatus.Succeeded,
                    message,
                    isWarning: true);
                return UiOperationResult<int>.Success(snapshot.Preview.Ciphers);
            }
            catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
            {
                operation.Complete(
                    UiOperationStatus.Failed,
                    UiExceptionPolicy.RequestTimeoutMessage);
                return UiOperationResult<int>.Failure(UiExceptionPolicy.RequestTimeoutMessage);
            }
            catch (Exception ex) when (IsRecoverableImportException(ex))
            {
                operation.Complete(UiOperationStatus.Failed, ImportFailureMessage);
                return UiOperationResult<int>.Failure(ImportFailureMessage);
            }
        }
    }

    public void ClearPreview()
    {
        _previewSnapshot = null;
        PreviewCipherCount = 0;
        PreviewFolderCount = 0;
        OnPropertyChanged(nameof(HasValidPreview));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanPreviewAgain));
    }

    partial void OnImportContentChanged(string value)
    {
        checked
        {
            _contentVersion++;
        }

        ClearPreview();
    }

    partial void OnSelectedImportFormatChanged(ImportFormat value) => ClearPreview();

    partial void OnSelectedExportFormatChanged(ExportFormat value) =>
        OnPropertyChanged(nameof(ExportFormatDescription));

    private UiOperationResult ParsePreview(
        ImportExportOperationScope operation,
        ImportFormat format,
        long contentVersion,
        string content)
    {
        var preview = _import.Parse(format, content);
        if (preview.Error is not null)
        {
            _previewSnapshot = new ImportPreviewSnapshot(
                format,
                contentVersion,
                content,
                preview);
            PreviewCipherCount = 0;
            PreviewFolderCount = 0;
            OnPropertyChanged(nameof(HasValidPreview));
            OnPropertyChanged(nameof(CanImport));
            OnPropertyChanged(nameof(CanPreviewAgain));
            operation.Complete(UiOperationStatus.Failed, ImportParseFailureMessage);
            return UiOperationResult.Failure(ImportParseFailureMessage);
        }

        _previewSnapshot = new ImportPreviewSnapshot(
            format,
            contentVersion,
            content,
            preview);
        PreviewCipherCount = preview.Ciphers;
        PreviewFolderCount = preview.Folders;
        OnPropertyChanged(nameof(HasValidPreview));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanPreviewAgain));
        var message = $"文件已就绪：{preview.Ciphers} 个条目，{preview.Folders} 个文件夹。";
        operation.Complete(UiOperationStatus.Succeeded, message);
        return UiOperationResult.Success();
    }

    private bool IsActive(
        ImportExportOperationScope operation,
        ImportExportOperationKind expectedKind) =>
        ReferenceEquals(_activeOperation, operation)
        && operation.Kind == expectedKind
        && OperationStatus == UiOperationStatus.Busy;

    private void ConsumePreview(ImportPreviewSnapshot importedSnapshot)
    {
        if (!ReferenceEquals(_previewSnapshot, importedSnapshot)
            || importedSnapshot.Format != SelectedImportFormat
            || importedSnapshot.ContentVersion != _contentVersion)
        {
            return;
        }

        ImportContent = string.Empty;
        SelectedImportFileName = null;
    }

    private void ClearOperationMessages()
    {
        OperationMessage = null;
        OperationHasWarning = false;
    }

    private static bool IsRecoverableImportException(Exception exception) =>
        exception is InvalidDataException
            or FormatException
            or InvalidOperationException
            or HttpRequestException
            or VaultWriteException
            or System.Security.Cryptography.CryptographicException
            or NotSupportedException;

    private static string DefaultMessage(UiOperationStatus status) => status switch
    {
        UiOperationStatus.Succeeded => "操作已完成。",
        UiOperationStatus.Cancelled => "操作已取消。",
        UiOperationStatus.Failed => "操作失败，请重试。",
        _ => string.Empty,
    };

    public void ClearSensitive()
    {
        Interlocked.Increment(ref _operationGeneration);
        _activeOperation?.Cancel();
        ImportContent = string.Empty;
        SelectedImportFileName = null;
        _previewSnapshot = null;
        PreviewCipherCount = 0;
        PreviewFolderCount = 0;
        OperationMessage = null;
        OperationHasWarning = false;
        if (_activeOperation is null)
        {
            CurrentOperation = null;
            OperationStatus = null;
            OperationMessage = null;
        }

        OnPropertyChanged(nameof(HasValidPreview));
        OnPropertyChanged(nameof(CanImport));
    }
}
