using App.Services;
using App.ViewModels;
using Core.Services;
using Xunit;

namespace App.Tests;

public class ImportExportViewModelTests
{
    // ── PreviewImportCommand ─────────────────────────────────────────────────

    [Fact]
    public void PreviewImport_Json_SetsCounts()
    {
        var vm = new ImportExportViewModel(new StubExport(), new StubImport(2, 1, null), new StubAccount(true));
        vm.ImportContent = "{...}";
        vm.SelectedImportFormat = ImportFormat.Json;

        vm.PreviewImportCommand.Execute(null);

        Assert.Equal(2, vm.PreviewCipherCount);
        Assert.Equal(1, vm.PreviewFolderCount);
        Assert.False(vm.HasImportError);
        Assert.Null(vm.ImportError);
    }

    [Fact]
    public void PreviewImport_Error_SetsError()
    {
        var vm = new ImportExportViewModel(new StubExport(), new StubImport(0, 0, "bad"), new StubAccount(true));
        vm.ImportContent = "not json";

        vm.PreviewImportCommand.Execute(null);

        Assert.True(vm.HasImportError);
        Assert.Equal("bad", vm.ImportError);
        Assert.Equal(0, vm.PreviewCipherCount);
        Assert.Equal(0, vm.PreviewFolderCount);
    }

    [Fact]
    public void PreviewImport_UsesSelectedFormat()
    {
        var stubImport = new StubImport(2, 1, null);
        var vm = new ImportExportViewModel(new StubExport(), stubImport, new StubAccount(true));
        vm.ImportContent = "a,b,c";
        vm.SelectedImportFormat = ImportFormat.Csv;

        vm.PreviewImportCommand.Execute(null);

        Assert.Equal(ImportFormat.Csv, stubImport.LastParseFormat);
    }

    // ── Export ───────────────────────────────────────────────────────────────

    [Fact]
    public void Export_UsesSelectedFormat()
    {
        var vm = new ImportExportViewModel(new StubExport(), new StubImport(0, 0, null), new StubAccount(true))
        {
            SelectedExportFormat = ExportFormat.Csv,
        };

        var text = vm.ExportToText();

        Assert.Equal("csv-marker", text);
    }

    [Fact]
    public void Export_DefaultsToJson()
    {
        var vm = new ImportExportViewModel(new StubExport(), new StubImport(0, 0, null), new StubAccount(true));
        Assert.Equal(ExportFormat.Json, vm.SelectedExportFormat);
        Assert.Equal("json-marker", vm.ExportToText());
    }

    // ── VerifyMasterPasswordAsync ────────────────────────────────────────────

    [Fact]
    public async Task VerifyMasterPasswordAsync_DelegatesToAccountService()
    {
        var vm = new ImportExportViewModel(new StubExport(), new StubImport(0, 0, null), new StubAccount(true));
        Assert.True(await vm.VerifyMasterPasswordAsync("correct"));

        var vmWrong = new ImportExportViewModel(new StubExport(), new StubImport(0, 0, null), new StubAccount(false));
        Assert.False(await vmWrong.VerifyMasterPasswordAsync("wrong"));
    }

    // ── DoImportAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task DoImportAsync_SetsResultMessage()
    {
        var vm = new ImportExportViewModel(new StubExport(), new StubImport(3, 0, null), new StubAccount(true))
        {
            ImportContent = "{...}",
        };
        vm.PreviewImportCommand.Execute(null);

        await vm.DoImportCommand.ExecuteAsync(null);

        Assert.Equal("已导入 3 个条目", vm.ResultMessage);
    }

    [Fact]
    public async Task DoImportAsync_ServiceThrows_SetsFriendlyResultMessage()
    {
        var vm = new ImportExportViewModel(new StubExport(), new ThrowingImport(), new StubAccount(true))
        {
            ImportContent = "{...}",
        };
        vm.PreviewImportCommand.Execute(null);

        await vm.DoImportCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ResultMessage);
        Assert.DoesNotContain("boom", vm.ResultMessage);
        Assert.Equal("导入失败，请确认文件仍有效且密码库已解锁。", vm.ResultMessage);
    }

    [Fact]
    public async Task DoImportAsync_NoOp_WhenImportContentEmpty()
    {
        var stubImport = new StubImport(5, 0, null);
        var vm = new ImportExportViewModel(new StubExport(), stubImport, new StubAccount(true))
        {
            ImportContent = string.Empty,
        };

        await vm.DoImportCommand.ExecuteAsync(null);

        Assert.False(stubImport.ImportAsyncCalled);
        Assert.Null(vm.ResultMessage);
    }

    [Fact]
    public async Task DoImportAsync_NoOp_WhenHasImportError()
    {
        var stubImport = new StubImport(0, 0, "bad");
        var vm = new ImportExportViewModel(new StubExport(), stubImport, new StubAccount(true))
        {
            ImportContent = "not json",
        };
        vm.PreviewImportCommand.Execute(null);

        await vm.DoImportCommand.ExecuteAsync(null);

        Assert.False(stubImport.ImportAsyncCalled);
    }

    // ── 快照失效（Task 8）──────────────────────────────────────────────────────

    [Fact]
    public void ContentChange_InvalidatesPreviewAndDisablesImport()
    {
        var vm = Build(new StableImport(2, 1));
        vm.ImportContent = "version-one";
        vm.PreviewImportCommand.Execute(null);
        Assert.True(vm.CanImport);

        vm.ImportContent = "version-two";

        Assert.False(vm.HasValidPreview);
        Assert.False(vm.CanImport);
    }

    [Fact]
    public void FormatChange_InvalidatesPreview()
    {
        var vm = Build(new StableImport(2, 1));
        vm.ImportContent = "content";
        vm.PreviewImportCommand.Execute(null);

        vm.SelectedImportFormat = ImportFormat.Csv;

        Assert.False(vm.CanImport);
    }

    [Fact]
    public void AbaContentChange_DoesNotReviveOldPreview()
    {
        var vm = Build(new StableImport(2, 1));
        vm.ImportContent = "A";
        vm.PreviewImportCommand.Execute(null);
        vm.ImportContent = "B";
        vm.ImportContent = "A";

        Assert.False(vm.CanImport);
    }

    [Fact]
    public async Task ReparseDrift_RejectsWithoutImporting()
    {
        var service = new DriftingImport();
        var vm = Build(service);
        vm.ImportContent = "content";
        vm.PreviewImportCommand.Execute(null);

        var result = await vm.DoImportAsync();

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.False(service.ImportCalled);
        Assert.Contains("预览已变化", vm.ImportError);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private static ImportExportViewModel Build(IVaultImportService import) =>
        new(new StubExport(), import, new StubAccount(true));

    private sealed class StableImport(int ciphers, int folders) : IVaultImportService
    {
        public ImportPreview Parse(ImportFormat format, string content) =>
            new(ciphers, folders, null);

        public Task<int> ImportAsync(
            ImportFormat format,
            string content,
            CancellationToken ct = default) =>
            Task.FromResult(ciphers);
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

    private sealed class StubExport : IVaultExportService
    {
        public string Export(ExportFormat format) => format == ExportFormat.Json ? "json-marker" : "csv-marker";
    }

    private sealed class StubImport(int ciphers, int folders, string? error) : IVaultImportService
    {
        public ImportFormat? LastParseFormat { get; private set; }
        public bool ImportAsyncCalled { get; private set; }

        public ImportPreview Parse(ImportFormat format, string content)
        {
            LastParseFormat = format;
            return new ImportPreview(ciphers, folders, error);
        }

        public Task<int> ImportAsync(ImportFormat format, string content, CancellationToken ct = default)
        {
            ImportAsyncCalled = true;
            return Task.FromResult(ciphers);
        }
    }

    private sealed class ThrowingImport : IVaultImportService
    {
        public ImportPreview Parse(ImportFormat format, string content) => new(1, 0, null);

        public Task<int> ImportAsync(ImportFormat format, string content, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class StubAccount(bool ok) : IAccountService
    {
        public Task UpdateNameAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

        public Task ChangePasswordAsync(string currentPassword, string newPassword, string? hint, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ChangeKdfAsync(string currentPassword, int newIterations, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> VerifyMasterPasswordAsync(string password, CancellationToken ct = default) =>
            Task.FromResult(ok);
    }
}
