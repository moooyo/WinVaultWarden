using App.Services;
using App.ViewModels;
using Core.Models;
using Xunit;

namespace App.Tests;

public class SecurityReportViewModelTests
{
    // ── LoadOffline ────────────────────────────────────────────────────────────

    [Fact]
    public void LoadOffline_PopulatesThreeSections()
    {
        var vm = new SecurityReportViewModel(new MockVaultHealthUiService());
        vm.LoadOffline();
        Assert.NotEmpty(vm.WeakItems);
        Assert.NotEmpty(vm.ReusedGroups);
        Assert.NotEmpty(vm.UnsecuredItems);
    }

    [Fact]
    public void LoadOffline_ClearsBeforeRefill()
    {
        var stub = new StubHealthUiService();
        stub.Report = new HealthReport(
            Reused: [new ReusedGroup(2, [new HealthItemRef("c1", "GitHub", "a@b.com"), new HealthItemRef("c2", "Twitter", "a@b.com")])],
            Weak: [new WeakFinding(new HealthItemRef("c3", "OldBank", null), 12)],
            Unsecured: [new UnsecuredFinding(new HealthItemRef("c3", "OldBank", null), "http://oldbank.example.com/login")]);

        var vm = new SecurityReportViewModel(stub);
        vm.LoadOffline();
        Assert.Single(vm.ReusedGroups);
        Assert.Single(vm.WeakItems);
        Assert.Single(vm.UnsecuredItems);

        // Empty report → collections should clear
        stub.Report = new HealthReport(Reused: [], Weak: [], Unsecured: []);
        vm.LoadOffline();
        Assert.Empty(vm.ReusedGroups);
        Assert.Empty(vm.WeakItems);
        Assert.Empty(vm.UnsecuredItems);
    }

    [Fact]
    public void LoadOffline_ExposedItemsNotTouched()
    {
        var vm = new SecurityReportViewModel(new MockVaultHealthUiService());
        // ExposedItems starts empty and LoadOffline must not touch it
        Assert.Empty(vm.ExposedItems);
        vm.LoadOffline();
        Assert.Empty(vm.ExposedItems);
    }

    // ── RunExposedCheckCommand ─────────────────────────────────────────────────

    [Fact]
    public async Task RunExposedCheck_PopulatesExposedAndSetsChecked()
    {
        var vm = new SecurityReportViewModel(new MockVaultHealthUiService());
        vm.LoadOffline();
        await vm.RunExposedCheckCommand.ExecuteAsync(null);
        Assert.True(vm.ExposedChecked);
        Assert.NotEmpty(vm.ExposedItems);
        Assert.False(vm.IsCheckingExposed);
    }

    [Fact]
    public async Task RunExposedCheck_ResetsIsCheckingExposedOnSuccess()
    {
        var vm = new SecurityReportViewModel(new MockVaultHealthUiService());
        await vm.RunExposedCheckCommand.ExecuteAsync(null);
        Assert.False(vm.IsCheckingExposed);
    }

    [Fact]
    public async Task RunExposedCheck_ServiceThrows_SetsExposedError_ExposedCheckedFalse()
    {
        var vm = new SecurityReportViewModel(new ThrowingHealthUiService("breach API unavailable"));
        await vm.RunExposedCheckCommand.ExecuteAsync(null);
        Assert.False(vm.ExposedChecked);
        Assert.False(vm.IsCheckingExposed);
        Assert.NotNull(vm.ExposedError);
        Assert.False(string.IsNullOrEmpty(vm.ExposedError));
        Assert.Empty(vm.ExposedItems);
    }

    [Fact]
    public async Task RunExposedCheck_ServiceThrows_IncludesExceptionMessage()
    {
        var msg = "HaveIBeenPwned is down";
        var vm = new SecurityReportViewModel(new ThrowingHealthUiService(msg));
        await vm.RunExposedCheckCommand.ExecuteAsync(null);
        Assert.Contains(msg, vm.ExposedError);
    }

    // ── Fakes ──────────────────────────────────────────────────────────────────

    private sealed class StubHealthUiService : IVaultHealthUiService
    {
        public HealthReport Report { get; set; } = new(Reused: [], Weak: [], Unsecured: []);

        public HealthReport AnalyzeOffline() => Report;

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExposedFinding>>([]);
    }

    private sealed class ThrowingHealthUiService : IVaultHealthUiService
    {
        private readonly string _message;

        public ThrowingHealthUiService(string message) => _message = message;

        public HealthReport AnalyzeOffline() => new(Reused: [], Weak: [], Unsecured: []);

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException(_message);
    }
}
