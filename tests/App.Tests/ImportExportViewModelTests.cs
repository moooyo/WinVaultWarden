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

        await vm.DoImportCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ResultMessage);
        Assert.Contains("boom", vm.ResultMessage);
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

    // ── Fakes ────────────────────────────────────────────────────────────────

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
