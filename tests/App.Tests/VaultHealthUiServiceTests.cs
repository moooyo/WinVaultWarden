using App.Services;
using Core.Models;
using Core.Services;
using Vault;
using Xunit;

namespace App.Tests;

public class VaultHealthUiServiceTests
{
    // ── 转发测试 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeOfflineAsync_ForwardsToCoreService()
    {
        var session = new VaultSession();
        session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://vault.example"));
        var core = new RecordingVaultHealthService();
        var ui = new VaultHealthUiService(core, session);
        await ui.AnalyzeOfflineAsync();
        Assert.Equal("analyzeOffline", core.LastOp);
    }

    [Fact]
    public async Task CheckExposedAsync_ForwardsToCoreService()
    {
        var session = new VaultSession();
        session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://vault.example"));
        var core = new RecordingVaultHealthService();
        var ui = new VaultHealthUiService(core, session);
        await ui.CheckExposedAsync();
        Assert.Equal("checkExposed", core.LastOp);
    }

    [Fact]
    public async Task AnalyzeOfflineAsync_ReportsStartAndCompleteProgress()
    {
        var session = new VaultSession();
        session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://vault.example"));
        var core = new RecordingVaultHealthService();
        var ui = new VaultHealthUiService(core, session);
        // 用同步 IProgress<T> 假实现而非 System.Progress<T>:后者经 SynchronizationContext.Post
        // 排队到线程池,在无消息泵的测试线程上会产生竞态。这里只关心"报告了 0 和 100 两次进度"。
        var progress = new SynchronousProgress<HealthAnalysisProgress>();

        await ui.AnalyzeOfflineAsync(progress);

        Assert.Contains(progress.Values, u => u.Completed == 0);
        Assert.Contains(progress.Values, u => u.Completed == 100);
    }

    [Fact]
    public async Task AnalyzeOfflineAsync_RunsOnBackgroundThread()
    {
        var session = new VaultSession();
        session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://vault.example"));
        var callingThreadId = Environment.CurrentManagedThreadId;
        var core = new ThreadCapturingHealthService();
        var ui = new VaultHealthUiService(core, session);

        await ui.AnalyzeOfflineAsync();

        Assert.NotNull(core.AnalysisThreadId);
        Assert.NotEqual(callingThreadId, core.AnalysisThreadId);
    }

    [Fact]
    public async Task LockWhileAnalysisRuns_DropsFinishedReport()
    {
        var session = new VaultSession();
        session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://vault.example"));
        var service = new BlockingHealthService();
        var ui = new VaultHealthUiService(service, session);

        var analysis = ui.AnalyzeOfflineAsync();
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.Lock();
        service.Release.Set();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => analysis);
    }

    // ── Mock 冒烟测试 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MockAnalyzeOfflineAsync_ReturnsNonEmptySections()
    {
        var mock = new MockVaultHealthUiService();
        var report = await mock.AnalyzeOfflineAsync();
        Assert.NotNull(report);
        Assert.NotEmpty(report.Reused);
        Assert.NotEmpty(report.Weak);
        Assert.NotEmpty(report.Unsecured);
    }

    [Fact]
    public async Task MockCheckExposedAsync_ReturnsNonEmptyList()
    {
        var mock = new MockVaultHealthUiService();
        var findings = await mock.CheckExposedAsync();
        Assert.NotEmpty(findings);
    }

    // ── Fakes ──────────────────────────────────────────────────────────────────

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class RecordingVaultHealthService : IVaultHealthService
    {
        public string LastOp { get; private set; } = "";

        public HealthReport AnalyzeOffline()
        {
            LastOp = "analyzeOffline";
            return new HealthReport([], [], []);
        }

        public Task<BreachCheckResult> CheckExposureAsync(CancellationToken ct = default)
        {
            LastOp = "checkExposure";
            return Task.FromResult(new BreachCheckResult(
                BreachCheckStatus.Succeeded,
                [],
                SuccessfulQueries: 1,
                FailedQueries: 0));
        }

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default)
        {
            LastOp = "checkExposed";
            return Task.FromResult<IReadOnlyList<ExposedFinding>>([]);
        }
    }

    private sealed class ThreadCapturingHealthService : IVaultHealthService
    {
        public int? AnalysisThreadId { get; private set; }

        public HealthReport AnalyzeOffline()
        {
            AnalysisThreadId = Environment.CurrentManagedThreadId;
            return new HealthReport([], [], []);
        }

        public Task<BreachCheckResult> CheckExposureAsync(CancellationToken ct = default) =>
            Task.FromResult(BreachCheckResult.Unknown);

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExposedFinding>>([]);
    }

    private sealed class BlockingHealthService : IVaultHealthService
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);

        public HealthReport AnalyzeOffline()
        {
            Started.SetResult();
            Release.Wait();
            return new HealthReport([], [], []);
        }

        public Task<BreachCheckResult> CheckExposureAsync(CancellationToken ct = default) =>
            Task.FromResult(BreachCheckResult.Unknown);

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExposedFinding>>([]);
    }
}
