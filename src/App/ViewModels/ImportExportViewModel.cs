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
    ImportPreview Preview)
{
    public ImportFormat EffectiveFormat { get; init; } = Format;
    public string? DestinationFolderId { get; init; }
}

public sealed partial record ImportFolderOption(string? Id, string DisplayName);

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
        string? destinationFolderId,
        long generation)
    {
        _owner = owner;
        _gateLease = gateLease;
        Kind = kind;
        ExportFormat = exportFormat;
        ImportFormat = importFormat;
        DestinationFolderId = destinationFolderId;
        Generation = generation;
    }

    public ImportExportOperationKind Kind { get; }
    public ExportFormat ExportFormat { get; }
    public ImportFormat ImportFormat { get; }
    public string? DestinationFolderId { get; }
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
    private static readonly string ImportParseFailureMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE9DE3F1E769702CA);
    private static readonly string UnsupportedImportFileMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE136B59CEFD6DCF1);
    private static readonly string MissingPreviewMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8CBF77F2DEB2F6C2);
    private static readonly string PreviewChangedMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5B0C712B61440CC0);
    private static readonly string ImportFailureMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5A6B2AEC9F19AAE7);
    private static readonly string ExportPolicyMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0952F57C2E79C7C8);

    private readonly IVaultExportService _export;
    private readonly IVaultImportService _import;
    private readonly IAccountService _account;
    private readonly AsyncOperationGate _operations = new();
    private ImportExportOperationScope? _activeOperation;
    private long _operationGeneration;
    private long _contentVersion;
    private ImportPreviewSnapshot? _previewSnapshot;
    private bool _contentIsPrepared;
    private int _importDestinationIndex;

    public IReadOnlyList<ImportFolderOption> DestinationFolders { get; private set; } =
        (ImportFolderOption[])[new(null, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text18183B6D7B7DD4A5))];

    public int ImportDestinationIndex
    {
        get => _importDestinationIndex;
        set
        {
            if (value < 0 || value >= DestinationFolders.Count
                || !SetProperty(ref _importDestinationIndex, value))
                return;
            ClearPreview();
            OnPropertyChanged(nameof(SelectedDestinationFolderId));
        }
    }

    public string? SelectedDestinationFolderId => DestinationFolders[_importDestinationIndex].Id;

    public string ImportFormatNotice => SelectedImportFormat switch
    {
        ImportFormat.KeePassXml => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text998DBB95BA0E67C7),
        ImportFormat.OnePasswordArchive => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text29800B60C7D39D88),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text23B27590F6562AA8),
    };

    public void RefreshDestinationFolders()
    {
        var selectedId = SelectedDestinationFolderId;
        var folders = _import.GetDestinationFolders();
        DestinationFolders = new ImportFolderOption[] { new(null, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text18183B6D7B7DD4A5)) }
            .Concat(folders.Select(folder => new ImportFolderOption(folder.Id, folder.Name)))
            .ToArray();
        var index = Array.FindIndex(DestinationFolders.ToArray(), folder => folder.Id == selectedId);
        _importDestinationIndex = Math.Max(0, index);
        OnPropertyChanged(nameof(DestinationFolders));
        OnPropertyChanged(nameof(ImportDestinationIndex));
        OnPropertyChanged(nameof(SelectedDestinationFolderId));
        if (selectedId != SelectedDestinationFolderId)
            ClearPreview();
    }

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

    public string ImportFileLabel => SelectedImportFileName ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA96F374E86BC73E4);
    public bool CanPreviewAgain => CanOperate && !string.IsNullOrWhiteSpace(ImportContent) && !HasValidPreview;
    public string ExportFormatDescription => SelectedExportFormat switch
    {
        ExportFormat.Json => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC9EE5984992B41C1),
        ExportFormat.Csv => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC332A55019CBC862),
        ExportFormat.PasswordProtectedJson => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBA8F3B11E13EFDEE),
        ExportFormat.AccountRestrictedJson => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFDD729D55DEB228E),
        _ => string.Empty,
    };

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
        ImportExportOperationKind.ChooseImportFile => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text103107DB12F97D09),
        ImportExportOperationKind.PreviewImport => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7B51EB1796E6004D),
        ImportExportOperationKind.Import => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBBE7B053F2BED58D),
        ImportExportOperationKind.Export => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7E9D698F0A465F9B),
        _ => string.Empty,
    };

    public bool HasValidPreview =>
        _previewSnapshot is { } snapshot
        && snapshot.Format == SelectedImportFormat
        && snapshot.ContentVersion == _contentVersion
        && snapshot.DestinationFolderId == SelectedDestinationFolderId
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
                SelectedDestinationFolderId,
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

    public static bool IsValidBackupPassword(string? password, string? confirmation) =>
        !string.IsNullOrWhiteSpace(password)
        && password.Length is >= 12 and <= 1024
        && string.Equals(password, confirmation, StringComparison.Ordinal);

    public Task<string> ExportToTextAsync(
        ExportFormat format,
        string? password,
        CancellationToken ct = default) =>
        _export.ExportProtectedAsync(format, password, ct);

    public ImportProtection InspectImportProtection(string content) =>
        _import.InspectJsonProtection(content);

    public Task<string> UnlockImportJsonAsync(
        string content,
        string? password,
        CancellationToken ct = default) =>
        _import.UnlockJsonAsync(content, password, ct);

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

        if (string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase))
        {
            format = ImportFormat.KeePassXml;
            return true;
        }

        if (string.Equals(extension, ".1pux", StringComparison.OrdinalIgnoreCase))
        {
            format = ImportFormat.OnePasswordArchive;
            return true;
        }

        return false;
    }

    public static bool TryResolveImportFormat(string? fileName, ImportFormat selectedFormat, out ImportFormat format)
    {
        if (!TryGetImportFormat(fileName, out format))
            return false;
        if (format == ImportFormat.Csv
            && selectedFormat is ImportFormat.ChromeCsv or ImportFormat.EdgeCsv or ImportFormat.FirefoxCsv or ImportFormat.LastPassCsv)
            format = selectedFormat;
        return true;
    }

    public static string ImportFileExtension(ImportFormat format) => format switch
    {
        ImportFormat.Json => ".json",
        ImportFormat.OnePasswordArchive => ".1pux",
        ImportFormat.KeePassXml => ".xml",
        _ => ".csv",
    };

    public Task<string> PrepareImportFileAsync(ImportFormat format, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) =>
        _import.PrepareFileAsync(format, bytes, ct);

    public Task<ImportPreview> PrepareImportPreviewAsync(
        ImportExportOperationScope operation,
        string canonicalJson) =>
        Task.Run(() => _import.Parse(
            ImportFormat.Json,
            canonicalJson,
            new VaultImportOptions(operation.DestinationFolderId)), operation.Token);

    public UiOperationResult ApplyPreparedImportFile(
        ImportExportOperationScope operation,
        string fileName,
        ImportFormat sourceFormat,
        string canonicalJson,
        ImportPreview? preparedPreview = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(canonicalJson);
        if (!IsActive(operation, ImportExportOperationKind.ChooseImportFile))
            return UiOperationResult.Cancelled();
        SelectedImportFormat = sourceFormat;
        ImportContent = canonicalJson;
        _contentIsPrepared = true;
        SelectedImportFileName = Path.GetFileName(fileName);
        return ParsePreview(operation, sourceFormat, _contentVersion, canonicalJson, ImportFormat.Json, preparedPreview);
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

        if (!TryResolveImportFormat(fileName, operation.ImportFormat, out var format))
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
    private async Task PreviewImportAsync() => _ = await PreviewCurrentImportAsync();

    public async Task<UiOperationResult> PreviewCurrentImportAsync()
    {
        var started = BeginOperation(ImportExportOperationKind.PreviewImport);
        if (!started.IsSuccess || started.Value is not { } operation)
            return UiOperationResult.Busy();
        using (operation)
        {
            var content = ImportContent;
            var contentVersion = _contentVersion;
            var effectiveFormat = _contentIsPrepared ? ImportFormat.Json : operation.ImportFormat;
            try
            {
                var preview = await Task.Run(() => _import.Parse(
                    effectiveFormat,
                    content,
                    new VaultImportOptions(operation.DestinationFolderId)), operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                return ParsePreview(operation, operation.ImportFormat, contentVersion, content, effectiveFormat, preview);
            }
            catch (OperationCanceledException exception) when (UiExceptionPolicy.IsGenuineCancellation(exception, operation.Token))
            {
                operation.Complete(UiOperationStatus.Cancelled);
                return UiOperationResult.Cancelled();
            }
        }
    }

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
                ImportContent,
                _contentIsPrepared ? ImportFormat.Json : null);
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
                || snapshot.DestinationFolderId != operation.DestinationFolderId
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
                var options = new VaultImportOptions(snapshot.DestinationFolderId);
                var reparsed = await Task.Run(
                    () => _import.Parse(snapshot.EffectiveFormat, snapshot.Content, options),
                    linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (reparsed != snapshot.Preview)
                {
                    ClearPreview();
                    operation.Complete(UiOperationStatus.Failed, PreviewChangedMessage);
                    return UiOperationResult<int>.Failure(PreviewChangedMessage);
                }

                var count = await _import.ImportAsync(
                    snapshot.EffectiveFormat,
                    snapshot.Content,
                    options,
                    linked.Token);
                // A successful service return means the server has committed. Consume the
                // preview before observing a cancellation that arrives after that point.
                ConsumePreview(snapshot);
                if (operation.Generation != Volatile.Read(ref _operationGeneration))
                {
                    operation.Complete(UiOperationStatus.Cancelled);
                    return UiOperationResult<int>.Cancelled();
                }
                if (linked.IsCancellationRequested)
                {
                    string committedMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3366F10B442B821C);
                    operation.Complete(UiOperationStatus.Succeeded, committedMessage, isWarning: true);
                    return UiOperationResult<int>.Success(count);
                }
                var message = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextFDE0635BA5E74BE9,count);
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
                if (operation.Generation != Volatile.Read(ref _operationGeneration))
                {
                    operation.Complete(UiOperationStatus.Cancelled);
                    return UiOperationResult<int>.Cancelled();
                }
                var message = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3BEA929DF8ACAA4A);
                operation.Complete(
                    UiOperationStatus.Succeeded,
                    message,
                    isWarning: true);
                return UiOperationResult<int>.Success(snapshot.Preview.Ciphers);
            }
            catch (VaultImportOutcomeUncertainException)
            {
                ConsumePreview(snapshot);
                if (operation.Generation != Volatile.Read(ref _operationGeneration))
                {
                    operation.Complete(UiOperationStatus.Cancelled);
                    return UiOperationResult<int>.Cancelled();
                }
                var message = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text87E4B674198A95E8);
                operation.Complete(UiOperationStatus.Failed, message);
                return UiOperationResult<int>.Failure(message);
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
        _contentIsPrepared = false;
        checked
        {
            _contentVersion++;
        }

        ClearPreview();
    }

    partial void OnSelectedImportFormatChanged(ImportFormat value)
    {
        _contentIsPrepared = false;
        ClearPreview();
        OnPropertyChanged(nameof(ImportFormatNotice));
    }

    partial void OnSelectedExportFormatChanged(ExportFormat value) =>
        OnPropertyChanged(nameof(ExportFormatDescription));

    private UiOperationResult ParsePreview(
        ImportExportOperationScope operation,
        ImportFormat format,
        long contentVersion,
        string content,
        ImportFormat? effectiveFormat = null,
        ImportPreview? preparedPreview = null)
    {
        var resolvedFormat = effectiveFormat ?? format;
        var preview = preparedPreview ?? _import.Parse(resolvedFormat, content, new VaultImportOptions(operation.DestinationFolderId));
        if (operation.Generation != Volatile.Read(ref _operationGeneration)
            || contentVersion != _contentVersion
            || format != SelectedImportFormat)
        {
            operation.Complete(UiOperationStatus.Cancelled);
            return UiOperationResult.Cancelled();
        }
        if (preview.Error is not null)
        {
            _previewSnapshot = new ImportPreviewSnapshot(
                format,
                contentVersion,
                content,
                preview)
            {
                EffectiveFormat = resolvedFormat,
                DestinationFolderId = operation.DestinationFolderId,
            };
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
            preview)
        {
            EffectiveFormat = resolvedFormat,
            DestinationFolderId = operation.DestinationFolderId,
        };
        PreviewCipherCount = preview.Ciphers;
        PreviewFolderCount = preview.Folders;
        OnPropertyChanged(nameof(HasValidPreview));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanPreviewAgain));
        var message = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text24A0A6DEFDEBF115,preview.Ciphers,preview.Folders);
        operation.Complete(UiOperationStatus.Succeeded, message);
        return UiOperationResult.Success();
    }

    private bool IsActive(
        ImportExportOperationScope operation,
        ImportExportOperationKind expectedKind) =>
        ReferenceEquals(_activeOperation, operation)
        && operation.Kind == expectedKind
        && operation.Generation == Volatile.Read(ref _operationGeneration)
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
        UiOperationStatus.Succeeded => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9CEA6B88615A53A5),
        UiOperationStatus.Cancelled => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text83F4727F2B4672A6),
        UiOperationStatus.Failed => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text825CFA17D9CDB472),
        _ => string.Empty,
    };

    public void ClearSensitive()
    {
        Interlocked.Increment(ref _operationGeneration);
        _activeOperation?.Cancel();
        ImportContent = string.Empty;
        SelectedImportFileName = null;
        DestinationFolders = (ImportFolderOption[])[new(null, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text18183B6D7B7DD4A5))];
        _importDestinationIndex = 0;
        OnPropertyChanged(nameof(DestinationFolders));
        OnPropertyChanged(nameof(ImportDestinationIndex));
        OnPropertyChanged(nameof(SelectedDestinationFolderId));
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
