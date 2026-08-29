using Api;
using App.Services;
using App.ViewModels;
using Core.Services;
using Xunit;

namespace App.Tests;

public class ImportExportViewModelTests
{
    [Fact]
    public void PreviewImport_SuccessPublishesCountsAndTerminalState()
    {
        var import = new StubImport(2, 1);
        var vm = Build(import);
        vm.ImportContent = "{...}";

        var result = vm.PreviewCurrentImport();

        Assert.Equal(UiOperationStatus.Succeeded, result.Status);
        Assert.Equal(UiOperationStatus.Succeeded, vm.OperationStatus);
        Assert.Equal(2, vm.PreviewCipherCount);
        Assert.Equal(1, vm.PreviewFolderCount);
        Assert.True(vm.CanImport);
        Assert.False(vm.HasFailedStatus);
    }

    [Fact]
    public void PreviewImport_ParseFailureUsesStableMessage()
    {
        var vm = Build(new StubImport(0, 0, "parser leaked C:\\secret\\vault.json"));
        vm.ImportContent = "not json";

        var result = vm.PreviewCurrentImport();

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(UiOperationStatus.Failed, vm.OperationStatus);
        Assert.DoesNotContain("secret", vm.OperationMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("无法解析导入文件，请确认文件格式和内容正确。", vm.OperationMessage);
        Assert.False(vm.CanImport);
    }

    [Fact]
    public void PreviewImport_UnexpectedExceptionFailsClosedAndReleasesGate()
    {
        var vm = Build(new ThrowingParseImport());
        vm.ImportContent = "bad";

        var exception = Assert.Throws<InvalidDataException>(() => vm.PreviewCurrentImport());
        var next = vm.BeginOperation(ImportExportOperationKind.Export);

        Assert.Contains("parse boom", exception.Message);
        Assert.Equal(UiOperationStatus.Succeeded, next.Status);
        next.Value!.Complete(UiOperationStatus.Cancelled);
    }

    [Fact]
    public void PreviewImport_UsesFormatCapturedAtStart()
    {
        var import = new StubImport(2, 1);
        var vm = Build(import);
        vm.ImportContent = "a,b,c";
        vm.SelectedImportFormat = ImportFormat.Csv;

        vm.PreviewImportCommand.Execute(null);

        Assert.Equal(ImportFormat.Csv, import.LastParseFormat);
    }

    [Fact]
    public void ApplySelectedImportFile_DetectsExtensionCaseInsensitively()
    {
        var import = new StubImport(3, 0);
        var vm = Build(import);
        var started = vm.BeginOperation(ImportExportOperationKind.ChooseImportFile);
        using var operation = started.Value!;

        var result = vm.ApplySelectedImportFile(operation, "vault.CSV", "a,b,c");

        Assert.Equal(UiOperationStatus.Succeeded, result.Status);
        Assert.Equal(ImportFormat.Csv, vm.SelectedImportFormat);
        Assert.Equal(ImportFormat.Csv, import.LastParseFormat);
        Assert.True(vm.CanImport);
    }

    [Fact]
    public void ApplySelectedImportFile_RejectsUnknownExtensionWithoutParsing()
    {
        var import = new StubImport(1, 0);
        var vm = Build(import);
        vm.ImportContent = "old payload";
        var started = vm.BeginOperation(ImportExportOperationKind.ChooseImportFile);
        using var operation = started.Value!;

        var result = vm.ApplySelectedImportFile(operation, "vault.txt", "payload");

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(0, import.ParseCalls);
        Assert.Equal(UiOperationStatus.Failed, vm.OperationStatus);
        Assert.Equal(string.Empty, vm.ImportContent);
        Assert.False(vm.CanImport);
    }

    [Theory]
    [InlineData("vault.json", ImportFormat.Json)]
    [InlineData("VAULT.JSON", ImportFormat.Json)]
    [InlineData("vault.csv", ImportFormat.Csv)]
    [InlineData("VAULT.CSV", ImportFormat.Csv)]
    public void TryGetImportFormat_MapsSupportedExtensions(string fileName, ImportFormat expected)
    {
        Assert.True(ImportExportViewModel.TryGetImportFormat(fileName, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("vault")]
    [InlineData("vault.txt")]
    public void TryGetImportFormat_RejectsUnsupportedNames(string? fileName) =>
        Assert.False(ImportExportViewModel.TryGetImportFormat(fileName, out _));

    [Fact]
    public void ExportToText_UsesExplicitFormat()
    {
        var vm = Build(new StubImport(0, 0));

        Assert.Equal("csv-marker", vm.ExportToText(ExportFormat.Csv));
        Assert.Equal("json-marker", vm.ExportToText(ExportFormat.Json));
    }

    [Fact]
    public void ExportOperation_FreezesFormatAndRejectsConcurrentOperation()
    {
        var vm = Build(new StubImport(0, 0));
        vm.SelectedExportFormat = ExportFormat.Json;
        var started = vm.BeginOperation(ImportExportOperationKind.Export);
        using var operation = started.Value!;

        vm.SelectedExportFormat = ExportFormat.Csv;
        var concurrent = vm.BeginOperation(ImportExportOperationKind.ChooseImportFile);

        Assert.Equal(ExportFormat.Json, operation.ExportFormat);
        Assert.Equal("json-marker", vm.ExportToText(operation.ExportFormat));
        Assert.Equal(UiOperationStatus.Busy, concurrent.Status);
        Assert.Equal(UiOperationStatus.Busy, vm.OperationStatus);
    }

    [Fact]
    public async Task VerifyMasterPasswordAsync_DelegatesToAccountService()
    {
        Assert.True(await Build(new StubImport(0, 0), accountResult: true)
            .VerifyMasterPasswordAsync("correct"));
        Assert.False(await Build(new StubImport(0, 0), accountResult: false)
            .VerifyMasterPasswordAsync("wrong"));
    }

    [Fact]
    public async Task DoImportAsync_SuccessConsumesPreviewAndCannotRepeat()
    {
        var import = new StubImport(3, 0);
        var vm = Build(import);
        vm.ImportContent = "{...}";
        vm.PreviewCurrentImport();

        var first = await vm.DoImportAsync();
        var second = await vm.DoImportAsync();

        Assert.Equal(UiOperationStatus.Succeeded, first.Status);
        Assert.Equal(3, first.Value);
        Assert.Equal(UiOperationStatus.Failed, second.Status);
        Assert.Equal(1, import.ImportCalls);
        Assert.Equal(string.Empty, vm.ImportContent);
        Assert.False(vm.HasValidPreview);
        Assert.False(vm.CanImport);
    }

    [Fact]
    public async Task DoImportAsync_ServiceThrowsUsesStableMessageAndKeepsPreview()
    {
        var vm = Build(new ThrowingImport());
        vm.ImportContent = "{...}";
        vm.PreviewCurrentImport();

        var result = await vm.DoImportAsync();

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal("导入失败，请确认文件仍有效且密码库已解锁。", vm.OperationMessage);
        Assert.DoesNotContain("boom", vm.OperationMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.CanImport);
    }

    [Fact]
    public async Task DoImportAsync_RequestTimeoutIsFailedNotCancelled()
    {
        var vm = Build(new TimeoutImport());
        vm.ImportContent = "{...}";
        vm.PreviewCurrentImport();

        var result = await vm.DoImportAsync(CancellationToken.None);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(UiExceptionPolicy.RequestTimeoutMessage, result.Error);
        Assert.Equal(UiExceptionPolicy.RequestTimeoutMessage, vm.OperationMessage);
        Assert.True(vm.CanImport);
    }

    [Fact]
    public async Task DoImportAsync_GenuineCancellationIsExplicitAndKeepsPreview()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var vm = Build(new CancelledImport());
        vm.ImportContent = "{...}";
        vm.PreviewCurrentImport();

        var result = await vm.DoImportAsync(cts.Token);

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Equal(UiOperationStatus.Cancelled, vm.OperationStatus);
        Assert.Equal("操作已取消。", vm.OperationMessage);
        Assert.True(vm.CanImport);
    }

    [Fact]
    public async Task DoImportAsync_RefreshFailureConsumesCommittedPreview()
    {
        var import = new RefreshFailureImport();
        var vm = Build(import);
        vm.ImportContent = "{...}";
        vm.PreviewCurrentImport();

        var result = await vm.DoImportAsync();
        var completionMessage = vm.OperationMessage;
        var showedWarning = vm.HasWarningStatus;
        var retry = await vm.DoImportAsync();

        Assert.Equal(UiOperationStatus.Succeeded, result.Status);
        Assert.NotNull(completionMessage);
        Assert.Contains("本地数据刷新失败", completionMessage);
        Assert.True(showedWarning);
        Assert.Equal(UiOperationStatus.Failed, retry.Status);
        Assert.Equal(1, import.ImportCalls);
        Assert.False(vm.CanImport);
    }

    [Fact]
    public async Task DoImportAsync_WithoutPreviewReturnsFailedWithoutCallingService()
    {
        var import = new StubImport(5, 0);
        var vm = Build(import);

        var result = await vm.DoImportAsync();

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(0, import.ImportCalls);
        Assert.Equal(UiOperationStatus.Failed, vm.OperationStatus);
    }

    [Fact]
    public async Task DoImportAsync_ConcurrentAttemptReturnsBusy()
    {
        var import = new ControlledImport();
        var vm = Build(import);
        vm.ImportContent = "content";
        vm.PreviewCurrentImport();

        var firstTask = vm.DoImportAsync();
        await import.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await vm.DoImportAsync();

        Assert.Equal(UiOperationStatus.Busy, second.Status);
        Assert.Equal(1, import.ImportCalls);
        Assert.True(vm.IsBusy);

        import.Completion.SetResult(2);
        var first = await firstTask;
        Assert.Equal(UiOperationStatus.Succeeded, first.Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task DoImportAsync_UsesSnapshotFormatWhenSelectionChangesInFlight()
    {
        var import = new ControlledImport();
        var vm = Build(import);
        vm.SelectedImportFormat = ImportFormat.Json;
        vm.ImportContent = "content";
        vm.PreviewCurrentImport();

        var importTask = vm.DoImportAsync();
        await import.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.SelectedImportFormat = ImportFormat.Csv;
        import.Completion.SetResult(2);
        await importTask;

        Assert.Equal(ImportFormat.Json, import.LastImportFormat);
        Assert.False(vm.CanImport);
    }

    [Fact]
    public async Task SharedGateBlocksPreviewAndImportWithoutSideEffects()
    {
        var import = new StubImport(2, 1);
        var vm = Build(import);
        vm.ImportContent = "content";
        var started = vm.BeginOperation(ImportExportOperationKind.Export);
        using var operation = started.Value!;

        var preview = vm.PreviewCurrentImport();
        var importResult = await vm.DoImportAsync();

        Assert.Equal(UiOperationStatus.Busy, preview.Status);
        Assert.Equal(UiOperationStatus.Busy, importResult.Status);
        Assert.Equal(0, import.ParseCalls);
        Assert.Equal(0, import.ImportCalls);
    }

    [Fact]
    public async Task ConcurrentScopeCompletionHasOneWinnerAndReleasesGate()
    {
        var vm = Build(new StubImport(0, 0));
        var started = vm.BeginOperation(ImportExportOperationKind.Export);
        var operation = started.Value!;
        using var barrier = new Barrier(2);

        var succeed = Task.Run(() =>
        {
            barrier.SignalAndWait();
            operation.Complete(UiOperationStatus.Succeeded, "done");
        });
        var cancel = Task.Run(() =>
        {
            barrier.SignalAndWait();
            operation.Dispose();
        });
        await Task.WhenAll(succeed, cancel);

        Assert.False(vm.IsBusy);
        Assert.True(vm.OperationStatus is UiOperationStatus.Succeeded or UiOperationStatus.Cancelled);
        var next = vm.BeginOperation(ImportExportOperationKind.Import);
        Assert.Equal(UiOperationStatus.Succeeded, next.Status);
        next.Value!.Complete(UiOperationStatus.Cancelled);
    }

    [Fact]
    public async Task ClearSensitiveSuppressesLateCompletionAndEventuallyReleasesGate()
    {
        var import = new ControlledImport();
        var vm = Build(import);
        vm.ImportContent = "content";
        vm.PreviewCurrentImport();
        var importTask = vm.DoImportAsync();
        await import.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.ClearSensitive();
        import.Completion.SetResult(2);
        var result = await importTask;

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Null(vm.OperationStatus);
        Assert.Null(vm.OperationMessage);
        Assert.Equal(string.Empty, vm.ImportContent);
        Assert.False(vm.CanImport);
        var next = vm.BeginOperation(ImportExportOperationKind.Export);
        Assert.Equal(UiOperationStatus.Succeeded, next.Status);
        next.Value!.Complete(UiOperationStatus.Cancelled);
    }

    [Fact]
    public async Task UnexpectedImportExceptionFailsClosedAndReleasesGate()
    {
        var vm = Build(new UnexpectedImport());
        vm.ImportContent = "content";
        vm.PreviewCurrentImport();

        await Assert.ThrowsAsync<ApplicationException>(() => vm.DoImportAsync());

        Assert.Equal(UiOperationStatus.Cancelled, vm.OperationStatus);
        var next = vm.BeginOperation(ImportExportOperationKind.Export);
        Assert.Equal(UiOperationStatus.Succeeded, next.Status);
        next.Value!.Complete(UiOperationStatus.Cancelled);
    }

    [Fact]
    public void DisposingOperationPublishesCancelledAndReleasesGate()
    {
        var vm = Build(new StubImport(0, 0));
        var first = vm.BeginOperation(ImportExportOperationKind.Export);

        first.Value!.Dispose();
        Assert.Equal(UiOperationStatus.Cancelled, vm.OperationStatus);
        var second = vm.BeginOperation(ImportExportOperationKind.ChooseImportFile);

        Assert.Equal(UiOperationStatus.Succeeded, second.Status);
        second.Value!.Complete(UiOperationStatus.Cancelled);
    }

    [Fact]
    public void BeginningOperationClearsPreviousTerminalMessages()
    {
        var vm = Build(new StubImport(0, 0, "bad"));
        vm.ImportContent = "bad";
        vm.PreviewCurrentImport();
        Assert.True(vm.HasFailedStatus);

        var next = vm.BeginOperation(ImportExportOperationKind.Export);

        Assert.Equal(UiOperationStatus.Busy, vm.OperationStatus);
        Assert.False(vm.HasFailedStatus);
        Assert.Null(vm.OperationMessage);
        Assert.Contains("正在导出", vm.BusyMessage);
        next.Value!.Complete(UiOperationStatus.Cancelled);
    }

    [Fact]
    public void ContentChangeInvalidatesPreviewIncludingAbaChange()
    {
        var vm = Build(new StubImport(2, 1));
        vm.ImportContent = "A";
        vm.PreviewCurrentImport();

        vm.ImportContent = "B";
        vm.ImportContent = "A";

        Assert.False(vm.HasValidPreview);
        Assert.False(vm.CanImport);
    }

    [Fact]
    public void FormatChangeInvalidatesPreview()
    {
        var vm = Build(new StubImport(2, 1));
        vm.ImportContent = "content";
        vm.PreviewCurrentImport();

        vm.SelectedImportFormat = ImportFormat.Csv;

        Assert.False(vm.HasValidPreview);
        Assert.False(vm.CanImport);
    }

    [Fact]
    public async Task ReparseDriftRejectsWithoutImporting()
    {
        var import = new DriftingImport();
        var vm = Build(import);
        vm.ImportContent = "content";
        vm.PreviewCurrentImport();

        var result = await vm.DoImportAsync();

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.False(import.ImportCalled);
        Assert.Contains("预览已变化", vm.OperationMessage);
        Assert.False(vm.CanImport);
    }

    private static ImportExportViewModel Build(
        IVaultImportService import,
        bool accountResult = true) =>
        new(new StubExport(), import, new StubAccount(accountResult));

    private sealed class StubExport : IVaultExportService
    {
        public string Export(ExportFormat format) =>
            format == ExportFormat.Json ? "json-marker" : "csv-marker";
    }

    private sealed class StubImport(
        int ciphers,
        int folders,
        string? error = null) : IVaultImportService
    {
        public ImportFormat? LastParseFormat { get; private set; }
        public ImportFormat? LastImportFormat { get; private set; }
        public int ParseCalls { get; private set; }
        public int ImportCalls { get; private set; }

        public ImportPreview Parse(ImportFormat format, string content)
        {
            ParseCalls++;
            LastParseFormat = format;
            return new ImportPreview(ciphers, folders, error);
        }

        public Task<int> ImportAsync(
            ImportFormat format,
            string content,
            CancellationToken ct = default)
        {
            ImportCalls++;
            LastImportFormat = format;
            return Task.FromResult(ciphers);
        }
    }

    private sealed class ControlledImport : IVaultImportService
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ImportCalls { get; private set; }
        public ImportFormat? LastImportFormat { get; private set; }

        public ImportPreview Parse(ImportFormat format, string content) => new(2, 1, null);

        public Task<int> ImportAsync(
            ImportFormat format,
            string content,
            CancellationToken ct = default)
        {
            ImportCalls++;
            LastImportFormat = format;
            Started.TrySetResult();
            return Completion.Task;
        }
    }

    private sealed class DriftingImport : IVaultImportService
    {
        private int _parseCalls;
        public bool ImportCalled { get; private set; }

        public ImportPreview Parse(ImportFormat format, string content) =>
            Interlocked.Increment(ref _parseCalls) == 1
                ? new ImportPreview(2, 1, null)
                : new ImportPreview(3, 1, null);

        public Task<int> ImportAsync(
            ImportFormat format,
            string content,
            CancellationToken ct = default)
        {
            ImportCalled = true;
            return Task.FromResult(2);
        }
    }

    private sealed class ThrowingParseImport : IVaultImportService
    {
        public ImportPreview Parse(ImportFormat format, string content) =>
            throw new InvalidDataException("parse boom C:\\secret\\vault.json");

        public Task<int> ImportAsync(
            ImportFormat format,
            string content,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingImport : IVaultImportService
    {
        public ImportPreview Parse(ImportFormat format, string content) => new(1, 0, null);

        public Task<int> ImportAsync(
            ImportFormat format,
            string content,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("import boom C:\\secret\\vault.json");
    }

    private sealed class UnexpectedImport : IVaultImportService
    {
        public ImportPreview Parse(ImportFormat format, string content) => new(1, 0, null);

        public Task<int> ImportAsync(
            ImportFormat format,
            string content,
            CancellationToken ct = default) =>
            throw new ApplicationException("unexpected invariant failure");
    }

    private sealed class CancelledImport : IVaultImportService
    {
        public ImportPreview Parse(ImportFormat format, string content) => new(1, 0, null);

        public Task<int> ImportAsync(
            ImportFormat format,
            string content,
            CancellationToken ct = default) =>
            Task.FromCanceled<int>(ct);
    }

    private sealed class TimeoutImport : IVaultImportService
    {
        public ImportPreview Parse(ImportFormat format, string content) => new(1, 0, null);

        public Task<int> ImportAsync(
            ImportFormat format,
            string content,
            CancellationToken ct = default) =>
            Task.FromException<int>(new TaskCanceledException(
                "request timed out",
                new RequestTimeoutException(
                    RequestTimeoutPhase.ControlPlane,
                    TimeSpan.FromSeconds(30)),
                CancellationToken.None));
    }

    private sealed class RefreshFailureImport : IVaultImportService
    {
        public int ImportCalls { get; private set; }

        public ImportPreview Parse(ImportFormat format, string content) => new(1, 0, null);

        public Task<int> ImportAsync(
            ImportFormat format,
            string content,
            CancellationToken ct = default)
        {
            ImportCalls++;
            return Task.FromException<int>(new VaultRefreshException(
                "server detail",
                new RequestTimeoutException(
                    RequestTimeoutPhase.ControlPlane,
                    TimeSpan.FromSeconds(30))));
        }
    }

    private sealed class StubAccount(bool result) : IAccountService
    {
        public Task UpdateNameAsync(string name, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ChangePasswordAsync(
            string currentPassword,
            string newPassword,
            string? hint,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ChangeKdfAsync(
            string currentPassword,
            int newIterations,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> VerifyMasterPasswordAsync(
            string password,
            CancellationToken ct = default) =>
            Task.FromResult(result);
    }
}
