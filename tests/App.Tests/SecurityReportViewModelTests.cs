using Api;
using App.Services;
using App.ViewModels;
using Core.Models;
using Xunit;

namespace App.Tests;

public class SecurityReportViewModelTests
{
    [Fact]
    public async Task ReusedGroups_ProjectIndependentItemSnapshotsWithExactNavigationIds()
    {
        var sourceItems = new[]
        {
            new HealthItemRef("login-a", "Same name", "first@example.test"),
            new HealthItemRef("login-b", "Same name", "second@example.test"),
        };
        var service = new StubHealthUiService
        {
            Report = new HealthReport([new ReusedGroup(2, sourceItems)], [], []),
        };
        var viewModel = new SecurityReportViewModel(service);

        await viewModel.LoadOfflineAsync();
        sourceItems[0] = new HealthItemRef("replaced", "Changed later", null);

        var group = Assert.Single(viewModel.ReusedGroups);
        Assert.Equal(2, group.Count);
        Assert.Equal(new[] { "login-a", "login-b" }, group.Items.Select(item => item.CipherId));
        Assert.Equal("first@example.test", group.Items[0].Username);
        viewModel.ClearSensitive();
        Assert.Empty(viewModel.ReusedGroups);
    }

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
        Assert.Equal("无法完成安全报告分析，请稍后重试。", vm.OfflineError);
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

    [Fact]
    public async Task LoadOffline_RequestTimeout_ReturnsFailureAndShowsSafeError()
    {
        var vm = new SecurityReportViewModel(new TimeoutHealthUiService());

        var result = await vm.LoadOfflineAsync(CancellationToken.None);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(UiExceptionPolicy.RequestTimeoutMessage, result.Error);
        Assert.Equal(UiExceptionPolicy.RequestTimeoutMessage, vm.OfflineError);
        Assert.Equal(DataPageState.Error, vm.OfflineState);
    }

    // ── RunExposedCheckCommand ─────────────────────────────────────────────────

    [Fact]
    public async Task RunExposedCheck_PopulatesExposedAndSetsChecked()
    {
        var vm = new SecurityReportViewModel(new MockVaultHealthUiService());
        await vm.LoadOfflineAsync();
        await vm.RunExposedCheckCommand.ExecuteAsync(null);
        Assert.Equal(ExposureCheckState.Succeeded, vm.ExposureState);
        Assert.True(vm.ExposedChecked);
        Assert.NotEmpty(vm.ExposedItems);
        Assert.False(vm.IsCheckingExposed);
        Assert.True(vm.ShowExposedCount);
    }

    [Fact]
    public async Task ReloadOfflineReport_PreservesExposureSnapshotAndRequiresRecheck()
    {
        var vm = new SecurityReportViewModel(new MockVaultHealthUiService());
        await vm.RunExposedCheckCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.ExposedItems);

        await vm.LoadOfflineAsync();

        Assert.NotEmpty(vm.ExposedItems);
        Assert.Equal(ExposureCheckState.Succeeded, vm.ExposureState);
        Assert.True(vm.ShowExposedCount);
        Assert.False(vm.ExposedChecked);
        Assert.Equal("待复查", vm.ExposureStatusLabel);
        Assert.Contains("待复查", vm.LastCheckedText);
        Assert.Empty(vm.ResolvedItems);
    }

    [Fact]
    public async Task CancelExposedCheck_ClearsPreviousFailureInsteadOfShowingItAsNotStarted()
    {
        var vm = new SecurityReportViewModel(new ThrowingHealthUiService("unavailable"));
        await vm.RunExposedCheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasExposedError);

        vm.CancelExposedCheck();

        Assert.Null(vm.ExposedError);
        Assert.Equal(ExposureCheckState.NotStarted, vm.ExposureState);
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
        Assert.Equal(ExposureCheckState.Failed, vm.ExposureState);
        Assert.Equal("无法完成暴露密码检查，请稍后重试。", vm.ExposedError);
        Assert.False(vm.ShowExposedCount);
        Assert.Empty(vm.ExposedItems);
    }

    [Fact]
    public async Task RunExposedCheck_ServiceThrows_DoesNotExposeExceptionMessage()
    {
        var msg = "HaveIBeenPwned is down";
        var vm = new SecurityReportViewModel(new ThrowingHealthUiService(msg));
        await vm.RunExposedCheckCommand.ExecuteAsync(null);
        Assert.Equal("无法完成暴露密码检查，请稍后重试。", vm.ExposedError);
        Assert.DoesNotContain(msg, vm.ExposedError);
    }

    [Fact]
    public async Task RunExposedCheck_RequestTimeout_ShowsSafeError()
    {
        var vm = new SecurityReportViewModel(new TimeoutHealthUiService());

        await vm.RunExposedCheckCommand.ExecuteAsync(null);

        Assert.Equal(UiExceptionPolicy.RequestTimeoutMessage, vm.ExposedError);
        Assert.Equal(ExposureCheckState.Failed, vm.ExposureState);
        Assert.False(vm.ExposedChecked);
        Assert.False(vm.IsCheckingExposed);
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
        Assert.Equal("发现 3 项可改进，暴露检查尚未运行", vm.SummaryText);
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
        Assert.Equal("离线检查未发现问题，暴露检查尚未运行", vm.SummaryText);
        Assert.False(vm.HasAnyIssue);
        Assert.Equal(SecuritySeverity.Unknown, vm.OverallSeverity);
        Assert.Equal(ExposureCheckState.NotStarted, vm.ExposureState);
        Assert.False(vm.ShowExposedCount);
    }

    [Fact]
    public async Task Aggregates_EmptyReport_BecomesGoodOnlyAfterExposureCheckSucceeds()
    {
        var vm = new SecurityReportViewModel(new StubHealthUiService());

        await vm.LoadOfflineAsync();
        await vm.RunExposedCheckCommand.ExecuteAsync(null);

        Assert.Equal(ExposureCheckState.Succeeded, vm.ExposureState);
        Assert.Equal("未发现问题", vm.SummaryText);
        Assert.Equal(SecuritySeverity.Good, vm.OverallSeverity);
        Assert.True(vm.ShowExposedCount);
        Assert.Equal("暴露密码，0 项", vm.ExposureMetricAccessibleName);
    }

    [Fact]
    public async Task ExposureCheck_CheckingAndFailedRemainNeutralWhenOfflineReportIsEmpty()
    {
        var service = new ControlledExposedHealthUiService();
        var vm = new SecurityReportViewModel(service);

        await vm.LoadOfflineAsync();
        var check = vm.RunExposedCheckCommand.ExecuteAsync(null);
        await service.Entered.Task;

        Assert.Equal(ExposureCheckState.Checking, vm.ExposureState);
        Assert.Equal("离线检查未发现问题，正在检查暴露密码", vm.SummaryText);
        Assert.Equal(SecuritySeverity.Unknown, vm.OverallSeverity);
        Assert.False(vm.ShowExposedCount);
        Assert.Equal("检查中", vm.ExposureStatusLabel);

        service.Fail(0, new InvalidOperationException("raw service detail"));
        await check;

        Assert.Equal(ExposureCheckState.Failed, vm.ExposureState);
        Assert.Equal("离线检查未发现问题，暴露检查失败", vm.SummaryText);
        Assert.Equal(SecuritySeverity.Unknown, vm.OverallSeverity);
        Assert.Equal("无法完成暴露密码检查，请稍后重试。", vm.ExposedError);
        Assert.Equal("检查失败", vm.ExposureStatusLabel);
    }

    // ── Finding B: 泄露检查(HIBP)缺代次守卫 → 锁定清理后迟到结果回写 ────────────────────
    // RunExposedCheck 原本无 requestId/CTS,ClearSensitive/离页只取消离线分析。锁屏清空后,
    // 一个仍在飞行中的 HIBP 结果会把 ExposedItems/ExposedChecked 重新写回。修复后:入口取消旧请求、
    // 捕获 local 代次;每次提交前校验代次;ClearSensitive 与离页调用取消。fake 故意忽略取消令牌,
    // 证明是"代次守卫"而非"服务响应取消"在丢弃迟到结果。

    [Fact]
    public async Task ClearSensitive_AfterExposedCheckStarts_DropsLateResult()
    {
        var service = new ControlledExposedHealthUiService();
        var vm = new SecurityReportViewModel(service);

        var check = vm.RunExposedCheckCommand.ExecuteAsync(null);
        await service.Entered.Task;

        vm.ClearSensitive();
        service.Complete(0, [new ExposedFinding(new HealthItemRef("c1", "GitHub", null), 5)]);
        await check;

        Assert.Empty(vm.ExposedItems);
        Assert.False(vm.ExposedChecked);
        Assert.False(vm.HasExposed);
        Assert.Equal(ExposureCheckState.NotStarted, vm.ExposureState);
    }

    [Fact]
    public async Task CancelExposedCheck_AfterStart_DropsLateResult()
    {
        var service = new ControlledExposedHealthUiService();
        var vm = new SecurityReportViewModel(service);

        var check = vm.RunExposedCheckCommand.ExecuteAsync(null);
        await service.Entered.Task;

        vm.CancelExposedCheck();
        service.Complete(0, [new ExposedFinding(new HealthItemRef("c1", "GitHub", null), 5)]);
        await check;

        Assert.Empty(vm.ExposedItems);
        Assert.False(vm.ExposedChecked);
        Assert.Equal(ExposureCheckState.NotStarted, vm.ExposureState);
    }

    // 守卫不能永久破坏功能:一次迟到结果被丢弃后,新的检查仍能正常填充。
    [Fact]
    public async Task RunExposedCheck_AfterStaleResultDropped_FreshCheckStillPopulates()
    {
        var service = new ControlledExposedHealthUiService();
        var vm = new SecurityReportViewModel(service);

        var stale = vm.RunExposedCheckCommand.ExecuteAsync(null);
        await service.Entered.Task;
        vm.CancelExposedCheck();
        service.Complete(0, [new ExposedFinding(new HealthItemRef("stale", "Old", null), 1)]);
        await stale;

        var fresh = vm.RunExposedCheckCommand.ExecuteAsync(null);
        await service.Entered.Task;
        service.Complete(1, [new ExposedFinding(new HealthItemRef("fresh", "New", null), 9)]);
        await fresh;

        var finding = Assert.Single(vm.ExposedItems);
        Assert.Equal("fresh", finding.Item.CipherId);
        Assert.True(vm.ExposedChecked);
        Assert.Equal(ExposureCheckState.Succeeded, vm.ExposureState);
    }

    [Fact]
    public async Task Overview_DeduplicatesByPriorityAndPreservesEachCategoryDetail()
    {
        var breached = new HealthItemRef("breached", "Zulu", "one@example.test");
        var alsoBreached = new HealthItemRef("also-breached", "Alpha", null);
        var reused = new HealthItemRef("reused", "Reused", null);
        var weak = new HealthItemRef("weak", "Weak", null);
        var unsecured = new HealthItemRef("unsecured", "Unsecured", null);
        var service = new StubHealthUiService
        {
            Report = new HealthReport(
                [new ReusedGroup(2, [breached, reused])],
                [new WeakFinding(breached, 1), new WeakFinding(reused, 1), new WeakFinding(weak, 1)],
                [new UnsecuredFinding(breached, "http://one.test"),
                    new UnsecuredFinding(weak, "http://two.test"),
                    new UnsecuredFinding(unsecured, "http://three.test")]),
            Exposed = [new ExposedFinding(alsoBreached, 2), new ExposedFinding(breached, 50)],
        };
        var vm = new SecurityReportViewModel(service);

        await vm.LoadOfflineAsync();
        await vm.RunExposedCheckCommand.ExecuteAsync(null);

        Assert.Equal("breached", vm.PriorityIssue?.CipherId);
        Assert.Equal("critical", vm.PriorityIssue?.Severity);
        Assert.Equal("还有 5 项需要处理", vm.CategorySummary);
        Assert.Equal(new[] { "breach", "reuse", "weak", "http" }, vm.IssueGroups.Select(group => group.Category));
        var remaining = vm.IssueGroups.SelectMany(group => group.Items).ToArray();
        Assert.Equal(new[] { "also-breached", "reused", "weak", "unsecured" }, remaining.Select(item => item.CipherId));
        Assert.DoesNotContain(remaining, item => item.CipherId == vm.PriorityIssue?.CipherId);
        Assert.Equal(4, remaining.Select(item => item.CipherId).Distinct().Count());

        vm.Category = "weak";
        Assert.False(vm.ShowPriority);
        Assert.Null(vm.PriorityIssue);
        Assert.Equal("弱密码", vm.PageTitle);
        Assert.Equal(3, Assert.Single(vm.IssueGroups).Count);
        Assert.Contains(Assert.Single(vm.IssueGroups).Items, item => item.CipherId == "breached");
        Assert.Empty(vm.ResolvedItems);

        vm.Category = "no2fa";
        Assert.Equal("all", vm.Category);
        Assert.DoesNotContain(vm.IssueGroups, group => group.Category == "no2fa");
    }

    [Fact]
    public async Task ResolvedItems_RequireEveryKnownIssueToDisappearAndResetOnLock()
    {
        var item = new HealthItemRef("entry", "Example", "user@example.test");
        var service = new StubHealthUiService
        {
            Report = new HealthReport([], [new WeakFinding(item, 1)], [new UnsecuredFinding(item, "http://example.test")]),
        };
        var vm = new SecurityReportViewModel(service);
        await vm.LoadOfflineAsync();

        service.Report = new HealthReport([], [], [new UnsecuredFinding(item, "http://example.test")]);
        await vm.RefreshReportsAsync();
        Assert.Empty(vm.ResolvedItems);
        Assert.Equal("http", vm.PriorityIssue?.Category);

        service.Report = new HealthReport([], [], []);
        await vm.RefreshReportsAsync();
        var resolved = Assert.Single(vm.ResolvedItems);
        Assert.Equal("entry", resolved.CipherId);
        Assert.Equal("weak", resolved.Category);
        Assert.False(string.IsNullOrWhiteSpace(resolved.ResultLabel));
        Assert.Equal(0, service.ExposureCalls);
        Assert.Contains("尚未检查", vm.EmptyMessage);

        vm.Category = "done";
        Assert.Equal("已处理 1 项", vm.CategorySummary);
        Assert.True(vm.HasVisibleIssues);
        Assert.Empty(vm.IssueGroups);
        Assert.False(vm.ShowPriority);

        vm.ClearSensitive();
        await vm.LoadOfflineAsync();
        vm.Category = "done";
        Assert.Empty(vm.ResolvedItems);
        Assert.False(vm.HasVisibleIssues);
        Assert.Equal("已处理 0 项", vm.CategorySummary);
    }

    [Fact]
    public async Task BreachResolution_RequiresFreshSuccessfulOnlineResult()
    {
        var item = new HealthItemRef("entry", "Example", null);
        var service = new StubHealthUiService
        {
            Report = new HealthReport([], [new WeakFinding(item, 1)], []),
            Exposed = [new ExposedFinding(item, 7)],
        };
        var vm = new SecurityReportViewModel(service);
        await vm.LoadOfflineAsync();
        await vm.RunExposedCheckCommand.ExecuteAsync(null);

        service.Report = new HealthReport([], [], []);
        service.Exposed = [];
        await vm.LoadOfflineAsync();
        Assert.Empty(vm.ResolvedItems);
        Assert.Equal("entry", Assert.Single(vm.ExposedItems).Item.CipherId);
        Assert.False(vm.ExposedChecked);

        service.ExposureError = new InvalidOperationException("unavailable");
        await vm.RefreshReportsAsync();
        Assert.Equal(ExposureCheckState.Failed, vm.ExposureState);
        Assert.Empty(vm.ResolvedItems);
        Assert.Equal("entry", vm.PriorityIssue?.CipherId);
        Assert.Equal(SecuritySeverity.Critical, vm.OverallSeverity);

        service.ExposureError = null;
        await vm.RefreshReportsAsync();
        Assert.True(vm.ExposedChecked);
        Assert.Empty(vm.ExposedItems);
        Assert.Equal("entry", Assert.Single(vm.ResolvedItems).CipherId);
        Assert.Equal("breach", Assert.Single(vm.ResolvedItems).Category);
        Assert.Equal("所有条目都通过了检查。", vm.EmptyMessage);
    }

    [Fact]
    public async Task RefreshReports_OnlyChecksExposureAfterAnExplicitCheckInTheCurrentSession()
    {
        var service = new StubHealthUiService();
        var vm = new SecurityReportViewModel(service);

        await vm.RefreshReportsAsync();
        await vm.RefreshReportsAsync();
        Assert.Equal(0, service.ExposureCalls);
        Assert.False(vm.ExposedChecked);
        Assert.Equal(SecuritySeverity.Unknown, vm.OverallSeverity);

        await vm.RunExposedCheckCommand.ExecuteAsync(null);
        await vm.RefreshReportsAsync();
        Assert.Equal(2, service.ExposureCalls);
        Assert.True(vm.ExposedChecked);

        vm.ClearSensitive();
        await vm.RefreshReportsAsync();
        Assert.Equal(2, service.ExposureCalls);
        Assert.False(vm.ExposedChecked);
    }

    [Fact]
    public async Task CancelExposedCheck_AfterCompletionPreservesTheSuccessfulSnapshot()
    {
        var vm = new SecurityReportViewModel(new MockVaultHealthUiService());
        await vm.LoadOfflineAsync();
        await vm.RunExposedCheckCommand.ExecuteAsync(null);
        var findings = vm.ExposedItems.ToArray();

        vm.CancelExposedCheck();

        Assert.Equal(findings, vm.ExposedItems.ToArray());
        Assert.True(vm.ExposedChecked);
        Assert.Equal(ExposureCheckState.Succeeded, vm.ExposureState);
    }

    [Fact]
    public async Task CommandCancellation_WhenServiceIgnoresToken_DoesNotStayCheckingOrClaimClean()
    {
        var service = new ControlledExposedHealthUiService();
        var vm = new SecurityReportViewModel(service);
        await vm.LoadOfflineAsync();
        var check = vm.RunExposedCheckCommand.ExecuteAsync(null);
        await service.Entered.Task;

        vm.RunExposedCheckCommand.Cancel();
        service.Complete(0, []);
        await check;

        Assert.False(vm.IsCheckingExposed);
        Assert.False(vm.ExposedChecked);
        Assert.Equal(ExposureCheckState.NotStarted, vm.ExposureState);
        Assert.Equal(SecuritySeverity.Unknown, vm.OverallSeverity);
        Assert.Contains("尚未检查", vm.EmptyMessage);
    }

    [Fact]
    public async Task CancelledRecheck_DoesNotReplaceThePreviousExposureSnapshotOrResolveIt()
    {
        var service = new ControlledExposedHealthUiService();
        var vm = new SecurityReportViewModel(service);
        await vm.LoadOfflineAsync();
        var initial = vm.RunExposedCheckCommand.ExecuteAsync(null);
        service.Complete(0, [new ExposedFinding(new HealthItemRef("entry", "Example", null), 4)]);
        await initial;

        var recheck = vm.RunExposedCheckCommand.ExecuteAsync(null);
        vm.CancelExposedCheck();
        service.Complete(1, []);
        await recheck;

        Assert.False(vm.IsCheckingExposed);
        Assert.False(vm.ExposedChecked);
        Assert.Equal("entry", Assert.Single(vm.ExposedItems).Item.CipherId);
        Assert.Equal("entry", vm.PriorityIssue?.CipherId);
        Assert.Empty(vm.ResolvedItems);
        Assert.Equal("待复查", vm.ExposureStatusLabel);
    }

    [Fact]
    public async Task CancellationDuringCollectionPublication_ProjectsOnlyTheCompleteCommittedSnapshot()
    {
        var first = new HealthItemRef("first", "First", null);
        var second = new HealthItemRef("second", "Second", null);
        var service = new StubHealthUiService { Report = new HealthReport([], [new WeakFinding(first, 1)], []) };
        var vm = new SecurityReportViewModel(service);
        await vm.LoadOfflineAsync();
        var cancelled = false;
        vm.WeakItems.CollectionChanged += (_, args) =>
        {
            if (!cancelled && args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                cancelled = true;
                vm.CancelOfflineAnalysis();
            }
        };
        service.Report = new HealthReport([], [new WeakFinding(second, 1)], [new UnsecuredFinding(first, "http://example.test")]);

        var result = await vm.LoadOfflineAsync();

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Empty(vm.ResolvedItems);
        Assert.Equal("second", vm.PriorityIssue?.CipherId);
        Assert.Equal("first", Assert.Single(Assert.Single(vm.IssueGroups).Items).CipherId);
        Assert.Equal("http", Assert.Single(vm.IssueGroups).Category);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearSensitive_DuringCollectionNotificationPurgesFindingsAndSessionBaseline(bool clearDuringResolution)
    {
        var first = new HealthItemRef("first", "First", null);
        var second = new HealthItemRef("second", "Second", null);
        var service = new StubHealthUiService
        {
            Report = new HealthReport([], [new WeakFinding(first, 1), new WeakFinding(second, 1)], []),
        };
        var vm = new SecurityReportViewModel(service);
        var cleared = false;
        void ClearOnAddition(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        {
            if (!cleared && args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                cleared = true;
                vm.ClearSensitive();
            }
        }

        if (clearDuringResolution)
        {
            await vm.LoadOfflineAsync();
            service.Report = new HealthReport([], [], []);
            vm.ResolvedItems.CollectionChanged += (_, _) => { };
            vm.ResolvedItems.CollectionChanged += ClearOnAddition;
        }
        else
        {
            vm.WeakItems.CollectionChanged += ClearOnAddition;
        }

        var result = await vm.LoadOfflineAsync();

        Assert.True(cleared);
        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Empty(vm.WeakItems);
        Assert.Empty(vm.ReusedGroups);
        Assert.Empty(vm.UnsecuredItems);
        Assert.Empty(vm.ExposedItems);
        Assert.Empty(vm.IssueGroups);
        Assert.Empty(vm.ResolvedItems);
        Assert.Null(vm.PriorityIssue);
        Assert.False(vm.HasVisibleIssues);
        Assert.Equal(0, vm.TotalIssueCount);

        service.Report = new HealthReport([], [], []);
        await vm.LoadOfflineAsync();
        vm.Category = "done";
        Assert.Empty(vm.ResolvedItems);
        Assert.Equal("已处理 0 项", vm.CategorySummary);
    }

    [Fact]
    public async Task MarkPasswordChanged_RemovesOnlyTheOldPasswordFindingUntilRechecked()
    {
        var changed = new HealthItemRef("changed", "Changed", null);
        var unchanged = new HealthItemRef("unchanged", "Unchanged", null);
        var service = new StubHealthUiService
        {
            Exposed = [new ExposedFinding(changed, 9), new ExposedFinding(unchanged, 3)],
        };
        var vm = new SecurityReportViewModel(service);
        await vm.LoadOfflineAsync();
        await vm.RunExposedCheckCommand.ExecuteAsync(null);

        vm.MarkPasswordChanged(changed.CipherId);

        Assert.Equal("unchanged", Assert.Single(vm.ExposedItems).Item.CipherId);
        Assert.Equal(1, vm.ExposedCount);
        Assert.Equal(1, vm.GetCategoryIssueCount("breach"));
        Assert.Equal(1, vm.OverviewIssueCount);
        Assert.Equal("unchanged", vm.PriorityIssue?.CipherId);
        Assert.False(vm.ExposedChecked);
        Assert.Equal("待复查", vm.ExposureStatusLabel);
        Assert.Equal(0, vm.ResolvedCount);
        Assert.Empty(vm.ResolvedItems);
        Assert.Equal(1, service.ExposureCalls);

        service.Exposed = [new ExposedFinding(unchanged, 3)];
        await vm.RunExposedCheckCommand.ExecuteAsync(null);

        Assert.True(vm.ExposedChecked);
        Assert.Equal(1, vm.ResolvedCount);
        Assert.Equal("changed", Assert.Single(vm.ResolvedItems).CipherId);
        Assert.Equal("breach", Assert.Single(vm.ResolvedItems).Category);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkPasswordChanged_DropsTheOldInFlightExposureResultAndFailure(bool failOldRequest)
    {
        var item = new HealthItemRef("entry", "Example", null);
        var service = new ControlledExposedHealthUiService();
        var vm = new SecurityReportViewModel(service);
        await vm.LoadOfflineAsync();
        var initial = vm.RunExposedCheckCommand.ExecuteAsync(null);
        service.Complete(0, [new ExposedFinding(item, 7)]);
        await initial;
        var stale = vm.RunExposedCheckCommand.ExecuteAsync(null);

        vm.MarkPasswordChanged(item.CipherId);
        if (failOldRequest)
            service.Fail(1, new InvalidOperationException("old request failure"));
        else
            service.Complete(1, [new ExposedFinding(item, 7)]);
        await stale;

        Assert.Empty(vm.ExposedItems);
        Assert.Equal(0, vm.ExposedCount);
        Assert.Equal(0, vm.OverviewIssueCount);
        Assert.Equal(0, vm.ResolvedCount);
        Assert.Empty(vm.ResolvedItems);
        Assert.Null(vm.PriorityIssue);
        Assert.Null(vm.ExposedError);
        Assert.False(vm.IsCheckingExposed);
        Assert.False(vm.ExposedChecked);
        Assert.Equal("待复查", vm.ExposureStatusLabel);
        Assert.Equal(SecuritySeverity.Unknown, vm.OverallSeverity);
    }

    [Fact]
    public async Task MarkPasswordChanged_BeforeAnyExposureCheckDoesNotAuthorizeAnOnlineRefresh()
    {
        var service = new StubHealthUiService();
        var vm = new SecurityReportViewModel(service);
        await vm.LoadOfflineAsync();

        vm.MarkPasswordChanged("entry");
        await vm.RefreshReportsAsync();

        Assert.Equal(0, service.ExposureCalls);
        Assert.Equal(ExposureCheckState.NotStarted, vm.ExposureState);
        Assert.Equal("未检查", vm.ExposureStatusLabel);
        Assert.False(vm.ExposedChecked);
        Assert.Equal(0, vm.ResolvedCount);
    }

    [Fact]
    public async Task MarkPasswordChanged_AfterLaterExposureCannotResolveAnOfflineBaselineWithoutRecheck()
    {
        var item = new HealthItemRef("entry", "Example", null);
        var service = new StubHealthUiService
        {
            Report = new HealthReport([], [new WeakFinding(item, 1)], []),
        };
        var vm = new SecurityReportViewModel(service);
        await vm.LoadOfflineAsync();
        await vm.RunExposedCheckCommand.ExecuteAsync(null);
        service.Report = new HealthReport([], [], []);
        service.Exposed = [new ExposedFinding(item, 5)];
        await vm.RefreshReportsAsync();

        vm.MarkPasswordChanged(item.CipherId);

        Assert.Empty(vm.ExposedItems);
        Assert.Equal(0, vm.ResolvedCount);
        Assert.Empty(vm.ResolvedItems);
        Assert.False(vm.ExposedChecked);

        service.Exposed = [];
        service.ExposureError = new InvalidOperationException("unavailable");
        await vm.RefreshReportsAsync();
        Assert.Equal(0, vm.ResolvedCount);
        Assert.False(vm.ExposedChecked);

        service.ExposureError = null;
        await vm.RefreshReportsAsync();
        Assert.True(vm.ExposedChecked);
        Assert.Equal("entry", Assert.Single(vm.ResolvedItems).CipherId);
        Assert.Equal(1, vm.ResolvedCount);
    }

    [Fact]
    public async Task ClearSensitive_ClearsPasswordChangeRecheckRequirementsForTheNextSession()
    {
        var item = new HealthItemRef("entry", "Example", null);
        var service = new StubHealthUiService { Exposed = [new ExposedFinding(item, 4)] };
        var vm = new SecurityReportViewModel(service);
        await vm.LoadOfflineAsync();
        await vm.RunExposedCheckCommand.ExecuteAsync(null);
        vm.MarkPasswordChanged(item.CipherId);

        vm.ClearSensitive();
        service.Report = new HealthReport([], [new WeakFinding(item, 1)], []);
        await vm.LoadOfflineAsync();
        service.Report = new HealthReport([], [], []);
        await vm.RefreshReportsAsync();

        Assert.Equal(1, service.ExposureCalls);
        Assert.Equal(ExposureCheckState.NotStarted, vm.ExposureState);
        Assert.Equal(1, vm.ResolvedCount);
        Assert.Equal("entry", Assert.Single(vm.ResolvedItems).CipherId);
        Assert.Equal("weak", Assert.Single(vm.ResolvedItems).Category);
    }

    // ── Fakes ──────────────────────────────────────────────────────────────────

    private sealed class StubHealthUiService : IVaultHealthUiService
    {
        public HealthReport Report { get; set; } = new(Reused: [], Weak: [], Unsecured: []);
        public IReadOnlyList<ExposedFinding> Exposed { get; set; } = [];
        public Exception? ExposureError { get; set; }
        public int ExposureCalls { get; private set; }

        public Task<HealthReport> AnalyzeOfflineAsync(
            IProgress<HealthAnalysisProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(Report);

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default)
        {
            ExposureCalls++;
            return ExposureError is { } error
                ? Task.FromException<IReadOnlyList<ExposedFinding>>(error)
                : Task.FromResult(Exposed);
        }
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
            Task.FromException<HealthReport>(new OperationCanceledException(
                "lease cancelled",
                new CancellationToken(canceled: true)));

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExposedFinding>>([]);
    }

    private sealed class TimeoutHealthUiService : IVaultHealthUiService
    {
        public Task<HealthReport> AnalyzeOfflineAsync(
            IProgress<HealthAnalysisProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromException<HealthReport>(RequestTimeout());

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(
            CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<ExposedFinding>>(RequestTimeout());

        private static TaskCanceledException RequestTimeout() => new(
            "request timed out",
            new RequestTimeoutException(
                RequestTimeoutPhase.ControlPlane,
                TimeSpan.FromSeconds(30)),
            CancellationToken.None);
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

    // 阻塞 CheckExposedAsync 直到 Complete 被调用;故意忽略 ct(证明代次守卫,而非取消协作,
    // 才是丢弃迟到结果的机制)。Entered 让测试等到服务确实进入 CheckExposedAsync 再触发清理/取消。
    private sealed class ControlledExposedHealthUiService : IVaultHealthUiService
    {
        private readonly List<TaskCompletionSource<IReadOnlyList<ExposedFinding>>> _calls = [];

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HealthReport> AnalyzeOfflineAsync(
            IProgress<HealthAnalysisProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(new HealthReport([], [], []));

        public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default)
        {
            var completion = new TaskCompletionSource<IReadOnlyList<ExposedFinding>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _calls.Add(completion);
            Entered.TrySetResult(true);
            return completion.Task;
        }

        public void Complete(int call, IReadOnlyList<ExposedFinding> findings) =>
            _calls[call].SetResult(findings);

        public void Fail(int call, Exception exception) =>
            _calls[call].SetException(exception);
    }
}
