using App.Services;
using Core.Models;
using Core.Services;
using Xunit;

namespace App.Tests;

public class VaultHealthUiServiceTests
{
    // ── 转发测试 ──────────────────────────────────────────────────────────────

    [Fact]
    public void AnalyzeOffline_ForwardsToCoreService()
    {
        var core = new RecordingVaultHealthService();
        var ui = new VaultHealthUiService(core);
        ui.AnalyzeOffline();
        Assert.Equal("analyzeOffline", core.LastOp);
    }

    [Fact]
    public async Task CheckExposedAsync_ForwardsToCoreService()
    {
        var core = new RecordingVaultHealthService();
        var ui = new VaultHealthUiService(core);
        await ui.CheckExposedAsync();
        Assert.Equal("checkExposed", core.LastOp);
    }

    // ── Mock 冒烟测试 ─────────────────────────────────────────────────────────

    [Fact]
    public void MockAnalyzeOffline_ReturnsNonEmptySections()
    {
        var mock = new MockVaultHealthUiService();
        var report = mock.AnalyzeOffline();
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

    // ── RecordingVaultHealthService ───────────────────────────────────────────

    private sealed class RecordingVaultHealthService : IVaultHealthService
    {
        public string LastOp { get; private set; } = "";

        public HealthReport AnalyzeOffline()
        {
            LastOp = "analyzeOffline";
            return new HealthReport([], [], []);
        }

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default)
        {
            LastOp = "checkExposed";
            return Task.FromResult<IReadOnlyList<ExposedFinding>>([]);
        }
    }
}
