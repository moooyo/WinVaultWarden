using App.Services;
using App.ViewModels;
using Core.Models;
using Xunit;

namespace App.Tests;

public class SecurityReportViewModelTests
{
    // ── LoadOfflineAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadOffline_PopulatesThreeSections()
    {
        var vm = new SecurityReportViewModel(new MockVaultHealthUiService());
        await vm.LoadOfflineAsync();
        Assert.NotEmpty(vm.WeakItems);
        Assert.NotEmpty(vm.ReusedGroups);
        Assert.NotEmpty(vm.UnsecuredItems);
    }

    [Fact]
    public async Task LoadOffline_ClearsBeforeRefill()
    {
        var stub = new StubHealthUiService();
        stub.Report = new HealthReport(
            Reused: [new ReusedGroup(2, [new HealthItemRef("c1", "GitHub", "a@b.com"), new HealthItemRef("c2", "Twitter", "a@b.com")])],
            Weak: [new WeakFinding(new HealthItemRef("c3", "OldBank", null), 12)],
            Unsecured: [new UnsecuredFinding(new HealthItemRef("c3", "OldBank", null), "http://oldbank.example.com/login")]);

        var vm = new SecurityReportViewModel(stub);
        await vm.LoadOfflineAsync();
        Assert.Single(vm.ReusedGroups);
        Assert.Single(vm.WeakItems);
        Assert.Single(vm.UnsecuredItems);

        // Empty report → collections should clear
        stub.Report = new HealthReport(Reused: [], Weak: [], Unsecured: []);
        await vm.LoadOfflineAsync();
        Assert.Empty(vm.ReusedGroups);
        Assert.Empty(vm.WeakItems);
        Assert.Empty(vm.UnsecuredItems);
    }

    [Fact]
    public async Task LoadOffline_ExposedItemsNotTouched()
    {
        var vm = new SecurityReportViewModel(new MockVaultHealthUiService());
        // ExposedItems starts empty and LoadOffline must not touch it
        Assert.Empty(vm.ExposedItems);
        await vm.LoadOfflineAsync();
        Assert.Empty(vm.ExposedItems);
    }

    [Fact]
    public async Task OlderOfflineResult_CannotOverwriteNewerResult()
    {
        var service = new ControlledHealthUiService();
        var vm = new SecurityReportViewModel(service);

        var oldLoad = vm.LoadOfflineAsync();
        var newLoad = vm.LoadOfflineAsync();
        service.Complete(
            call: 1,
            new HealthReport([], [new WeakFinding(
                new HealthItemRef("new", "New", null), 1)], []));
        await newLoad;
        service.Complete(
            call: 0,
            new HealthReport([], [new WeakFinding(
                new HealthItemRef("old", "Old", null), 1)], []));
        await oldLoad;

        var finding = Assert.Single(vm.WeakItems);
        Assert.Equal("new", finding.Item.CipherId);
    }

    [Fact]
    public async Task LoadOffline_ReportsLoadingThenContent()
    {
        var service = new ImmediateHealthUiService(
            new HealthReport([], [new WeakFinding(
                new HealthItemRef("c1", "One", null), 1)], []));
        var vm = new SecurityReportViewModel(service);

        var result = await vm.LoadOfflineAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(DataPageState.Content, vm.OfflineState);
        Assert.Equal(100, vm.OfflineProgress);
    }

    [Fact]
    public async Task OfflineState_RaisesShowNotificationsAcrossContentAndErrorTransitions()
    {
        var service = new SequencedHealthUiService(
            success: new HealthReport([], [new WeakFinding(
                new HealthItemRef("c1", "One", null), 1)], []),
            failureMessage: "本地安全报告分析失败");
        var vm = new SecurityReportViewModel(service);
        var notified = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
                notified.Add(e.PropertyName);
        };

        await vm.LoadOfflineAsync();

        Assert.Equal(DataPageState.Content, vm.OfflineState);
        Assert.Contains(nameof(SecurityReportViewModel.ShowOfflineLoading), notified);
        Assert.Contains(nameof(SecurityReportViewModel.ShowOfflineError), notified);
        Assert.Contains(nameof(SecurityReportViewModel.ShowOfflineContent), notified);

        notified.Clear();

        await vm.LoadOfflineAsync();

        Assert.Equal(DataPageState.Error, vm.OfflineState);
        Assert.Equal("本地安全报告分析失败", vm.OfflineError);
        Assert.Contains(nameof(SecurityReportViewModel.ShowOfflineLoading), notified);
        Assert.Contains(nameof(SecurityReportViewModel.ShowOfflineError), notified);
        Assert.Contains(nameof(SecurityReportViewModel.ShowOfflineContent), notified);
    }

    [Fact]
    public async Task CancelOfflineAnalysis_DropsLateResult()
    {
        var service = new ControlledHealthUiService();
        var vm = new SecurityReportViewModel(service);
        var load = vm.LoadOfflineAsync();

        vm.CancelOfflineAnalysis();
        service.Complete(0, new HealthReport([], [], []));
        var result = await load;

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Empty(vm.WeakItems);
    }

    [Fact]
    public async Task LoadOffline_ConsumesOperationCanceledException()
    {
        var vm = new SecurityReportViewModel(new CanceledHealthUiService());

        var result = await vm.LoadOfflineAsync();

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Equal(DataPageState.Loading, vm.OfflineState);
    }

    // ── RunExposedCheckCommand ─────────────────────────────────────────────────

    [Fact]
    public async Task RunExposedCheck_PopulatesExposedAndSetsChecked()
    {
        var vm = new SecurityReportViewModel(new MockVaultHealthUiService());
        await vm.LoadOfflineAsync();
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

    // ── HasX 空态布尔属性 ──────────────────────────────────────────────────────

    [Fact]
    public async Task HasReused_True_WhenReusedGroupsNonEmpty()
    {
        var vm = new SecurityReportViewModel(new MockVaultHealthUiService());
        await vm.LoadOfflineAsync();
        Assert.True(vm.HasReused);
    }

    [Fact]
    public async Task HasWeak_True_WhenWeakItemsNonEmpty()
    {
        var vm = new SecurityReportViewModel(new MockVaultHealthUiService());
        await vm.LoadOfflineAsync();
        Assert.True(vm.HasWeak);
    }

    [Fact]
    public async Task HasUnsecured_True_WhenUnsecuredItemsNonEmpty()
    {
        var vm = new SecurityReportViewModel(new MockVaultHealthUiService());
        await vm.LoadOfflineAsync();
        Assert.True(vm.HasUnsecured);
    }

    [Fact]
    public async Task HasReused_False_WhenEmptyReport()
    {
        var stub = new StubHealthUiService();
        // stub.Report defaults to empty
        var vm = new SecurityReportViewModel(stub);
        await vm.LoadOfflineAsync();
        Assert.False(vm.HasReused);
    }

    [Fact]
    public async Task HasWeak_False_WhenEmptyReport()
    {
        var stub = new StubHealthUiService();
        var vm = new SecurityReportViewModel(stub);
        await vm.LoadOfflineAsync();
        Assert.False(vm.HasWeak);
    }

    [Fact]
    public async Task HasUnsecured_False_WhenEmptyReport()
    {
        var stub = new StubHealthUiService();
        var vm = new SecurityReportViewModel(stub);
        await vm.LoadOfflineAsync();
        Assert.False(vm.HasUnsecured);
    }

    [Fact]
    public async Task HasExposed_True_AfterRunExposedCheckWithResults()
    {
        var vm = new SecurityReportViewModel(new MockVaultHealthUiService());
        await vm.RunExposedCheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasExposed);
    }

    [Fact]
    public async Task HasExposed_False_WhenExposedCheckReturnsEmpty()
    {
        var stub = new StubHealthUiService(); // CheckExposedAsync returns []
        var vm = new SecurityReportViewModel(stub);
        await vm.RunExposedCheckCommand.ExecuteAsync(null);
        Assert.False(vm.HasExposed);
    }

    // ── 聚合摘要属性（Hero 卡片） ──────────────────────────────────────────────

    [Fact]
    public async Task Aggregates_ReflectThreeIssues_SeverityCritical()
    {
        var stub = new StubHealthUiService();
        stub.Report = new HealthReport(
            Reused: [new ReusedGroup(2, [new HealthItemRef("c1", "GitHub", "a@b.com"), new HealthItemRef("c2", "Twitter", "a@b.com")])],
            Weak: [new WeakFinding(new HealthItemRef("c3", "OldBank", null), 12)],
            Unsecured: [new UnsecuredFinding(new HealthItemRef("c3", "OldBank", null), "http://oldbank.example.com/login")]);

        var vm = new SecurityReportViewModel(stub);
        await vm.LoadOfflineAsync();

        Assert.Equal(1, vm.ReusedCount);
        Assert.Equal(1, vm.WeakCount);
        Assert.Equal(1, vm.UnsecuredCount);
        Assert.Equal(0, vm.ExposedCount);
        Assert.Equal(3, vm.TotalIssueCount);
        Assert.Equal("发现 3 项可改进", vm.SummaryText);
        Assert.True(vm.HasAnyIssue);
        Assert.Equal(SecuritySeverity.Critical, vm.OverallSeverity);
    }

    [Fact]
    public async Task Aggregates_EmptyReport_SeverityGood()
    {
        var stub = new StubHealthUiService();
        // stub.Report defaults to empty

        var vm = new SecurityReportViewModel(stub);
        await vm.LoadOfflineAsync();

        Assert.Equal(0, vm.TotalIssueCount);
        Assert.Equal("未发现问题", vm.SummaryText);
        Assert.False(vm.HasAnyIssue);
        Assert.Equal(SecuritySeverity.Good, vm.OverallSeverity);
    }

    // ── Fakes ──────────────────────────────────────────────────────────────────

    private sealed class StubHealthUiService : IVaultHealthUiService
    {
        public HealthReport Report { get; set; } = new(Reused: [], Weak: [], Unsecured: []);

        public Task<HealthReport> AnalyzeOfflineAsync(
            IProgress<HealthAnalysisProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(Report);

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExposedFinding>>([]);
    }

    private sealed class ThrowingHealthUiService : IVaultHealthUiService
    {
        private readonly string _message;

        public ThrowingHealthUiService(string message) => _message = message;

        public Task<HealthReport> AnalyzeOfflineAsync(
            IProgress<HealthAnalysisProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(new HealthReport(Reused: [], Weak: [], Unsecured: []));

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException(_message);
    }

    // ControlledHealthUiService 故意忽略 cancellation token:专门证明 request-id
    // 会丢弃不合作的迟到结果，而不是依赖服务本身响应取消。
    private sealed class ControlledHealthUiService : IVaultHealthUiService
    {
        private readonly List<TaskCompletionSource<HealthReport>> _calls = [];

        public Task<HealthReport> AnalyzeOfflineAsync(
            IProgress<HealthAnalysisProgress>? progress = null,
            CancellationToken ct = default)
        {
            var completion = new TaskCompletionSource<HealthReport>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _calls.Add(completion);
            return completion.Task;
        }

        public void Complete(int call, HealthReport report) =>
            _calls[call].SetResult(report);

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExposedFinding>>([]);
    }

    private sealed class SequencedHealthUiService(HealthReport success, string failureMessage)
        : IVaultHealthUiService
    {
        private int _calls;

        public Task<HealthReport> AnalyzeOfflineAsync(
            IProgress<HealthAnalysisProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                progress?.Report(new HealthAnalysisProgress(100, 100, "分析完成"));
                return Task.FromResult(success);
            }

            return Task.FromException<HealthReport>(
                new VaultHealthUiException(failureMessage, new InvalidOperationException(failureMessage)));
        }

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExposedFinding>>([]);
    }

    private sealed class CanceledHealthUiService : IVaultHealthUiService
    {
        public Task<HealthReport> AnalyzeOfflineAsync(
            IProgress<HealthAnalysisProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromException<HealthReport>(new OperationCanceledException("lease cancelled"));

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExposedFinding>>([]);
    }

    private sealed class ImmediateHealthUiService(HealthReport report)
        : IVaultHealthUiService
    {
        public Task<HealthReport> AnalyzeOfflineAsync(
            IProgress<HealthAnalysisProgress>? progress = null,
            CancellationToken ct = default)
        {
            progress?.Report(new HealthAnalysisProgress(100, 100, "分析完成"));
            return Task.FromResult(report);
        }

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExposedFinding>>([]);
    }
}
