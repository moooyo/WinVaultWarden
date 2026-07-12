using System.Net;
using Api;
using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Core.Models;
using Core.Services;
using Vault;
using Xunit;

namespace App.Tests;

public class DevicesViewModelTests
{
    // ── 设备列表（原有行为，保留） ───────────────────────────────────────────

    [Fact]
    public void HasNoDevices_FalseWhenDevicesLoaded()
    {
        var vm = new DevicesViewModel(new MockDeviceUiService());

        Assert.NotEmpty(vm.Devices);
        Assert.False(vm.HasNoDevices);
    }

    [Fact]
    public void HasNoDevices_TrueWhenServiceReturnsNone()
    {
        var vm = new DevicesViewModel(new EmptyDeviceUiService());

        Assert.Empty(vm.Devices);
        Assert.True(vm.HasNoDevices);
    }

    [Fact]
    public void IsBusy_And_Error_AreSettableAndDefault()
    {
        var vm = new DevicesViewModel(new MockDeviceUiService());

        Assert.False(vm.IsBusy);
        Assert.Null(vm.Error);

        vm.IsBusy = true;
        vm.Error = "操作失败";

        Assert.True(vm.IsBusy);
        Assert.Equal("操作失败", vm.Error);
    }

    [Fact]
    public void HasError_FalseWhenErrorNull_TrueWhenErrorSet()
    {
        var vm = new DevicesViewModel(new MockDeviceUiService());
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        Assert.False(vm.HasError);

        vm.Error = "连接超时";

        Assert.True(vm.HasError);
        Assert.Contains(nameof(DevicesViewModel.HasError), notifications);

        vm.Error = null;

        Assert.False(vm.HasError);
    }

    // ── PendingRequests 初始状态 ─────────────────────────────────────────────

    [Fact]
    public void PendingRequests_EmptyOnConstruction()
    {
        var vm = new DevicesViewModel(new MockDeviceUiService());

        Assert.Empty(vm.PendingRequests);
    }

    // ── RefreshRequestsAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task RefreshRequestsAsync_FillsPendingRequests()
    {
        var fake = new FakeAuthRequestService();
        fake.AddRequest(new PendingAuthRequest("r1", "Android", "1.2.3.4", "2025-01-01T10:00:00Z", "pubkey-abc"));
        fake.AddRequest(new PendingAuthRequest("r2", "Windows", "5.6.7.8", "2025-06-15T08:30:00Z", "pubkey-xyz"));
        var authUi = new AuthRequestUiService(fake);
        var vm = new DevicesViewModel(new MockDeviceUiService(), authUi);

        await vm.RefreshRequestsAsync();

        Assert.Equal(2, vm.PendingRequests.Count);
        Assert.Equal("r1", vm.PendingRequests[0].Id);
        Assert.Equal("Android", vm.PendingRequests[0].DeviceTypeName);
        Assert.Equal("1.2.3.4", vm.PendingRequests[0].IpAddress);
        Assert.Equal("pubkey-abc", vm.PendingRequests[0].PublicKey);
        Assert.Equal("r2", vm.PendingRequests[1].Id);
    }

    [Fact]
    public async Task RefreshRequestsAsync_CreatedLabel_ParsedDateTimeOffset_FormatsCorrectly()
    {
        var fake = new FakeAuthRequestService();
        fake.AddRequest(new PendingAuthRequest("r1", "iOS", "1.1.1.1", "2025-03-05T09:05:00Z", "pk"));
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));

        await vm.RefreshRequestsAsync();

        // DateTimeOffset.Parse("2025-03-05T09:05:00Z").ToLocalTime() → 日期部分 yyyy/M/d HH:mm
        Assert.Matches(@"^\d{4}/\d{1,2}/\d{1,2} \d{2}:\d{2}$", vm.PendingRequests[0].CreatedLabel);
    }

    [Fact]
    public async Task RefreshRequestsAsync_CreatedLabel_UnparsableDate_RawString()
    {
        var fake = new FakeAuthRequestService();
        fake.AddRequest(new PendingAuthRequest("r1", "iOS", "1.1.1.1", "not-a-date", "pk"));
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));

        await vm.RefreshRequestsAsync();

        Assert.Equal("not-a-date", vm.PendingRequests[0].CreatedLabel);
    }

    [Fact]
    public async Task RefreshRequestsAsync_ClearsExistingBeforeRefill()
    {
        var fake = new FakeAuthRequestService();
        fake.AddRequest(new PendingAuthRequest("r1", "Android", "1.1.1.1", "2025-01-01T00:00:00Z", "pk"));
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));
        await vm.RefreshRequestsAsync();
        Assert.Single(vm.PendingRequests);

        fake.Clear();
        await vm.RefreshRequestsAsync();

        Assert.Empty(vm.PendingRequests);
    }

    [Fact]
    public async Task RefreshRequestsAsync_ServiceThrows_SetsError()
    {
        var fake = new ThrowingAuthRequestService("服务器错误");
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));

        await vm.RefreshRequestsAsync();

        Assert.NotNull(vm.Error);
        Assert.False(string.IsNullOrEmpty(vm.Error));
    }

    [Fact]
    public async Task RefreshRequestsAsync_NullService_SetsError()
    {
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(null));

        await vm.RefreshRequestsAsync();

        Assert.NotNull(vm.Error);
        Assert.False(string.IsNullOrEmpty(vm.Error));
    }

    [Fact]
    public async Task RefreshRequestsAsync_InDemoMode_ShowsEmptyPending_WithoutHittingNetworkOrError()
    {
        // 演示/离线会话:不得调用底层授权服务(否则 ThrowingAuthRequestService 会抛),
        // 且展示干净的空状态(无 Error)——而非在演示页留下「操作失败」提示。
        var session = new VaultSession();
        session.MarkDemo();
        var throwing = new AuthRequestUiService(new ThrowingAuthRequestService("network must not run"));
        var vm = new DevicesViewModel(new MockDeviceUiService(), throwing, session);

        await vm.RefreshRequestsAsync();

        Assert.Empty(vm.PendingRequests);
        Assert.True(vm.HasNoPendingRequests);
        Assert.Null(vm.Error);
    }

    // ── ApproveAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ApproveAsync_DelegatesAndRemovesItemFromList()
    {
        var fake = new FakeAuthRequestService();
        var req = new PendingAuthRequest("r1", "Android", "1.2.3.4", "2025-01-01T10:00:00Z", "pubkey-abc");
        fake.AddRequest(req);
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));
        await vm.RefreshRequestsAsync();
        var item = vm.PendingRequests.Single();

        await vm.ApproveAsync(item, (_, _) => Task.FromResult(true));

        Assert.Empty(vm.PendingRequests);
        Assert.Contains("r1", fake.ApprovedIds);
        Assert.Equal("pubkey-abc", fake.ApprovedPublicKeys["r1"]);
    }

    [Fact]
    public async Task ApproveAsync_ClearsError()
    {
        var fake = new FakeAuthRequestService();
        fake.AddRequest(new PendingAuthRequest("r1", "Android", "1.1.1.1", "2025-01-01T00:00:00Z", "pk"));
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));
        await vm.RefreshRequestsAsync();
        vm.Error = "旧错误";
        var item = vm.PendingRequests.Single();

        await vm.ApproveAsync(item, (_, _) => Task.FromResult(true));

        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task ApproveAsync_ServiceThrows_SetsError_ItemStaysInList()
    {
        var fake = new FakeAuthRequestService
        {
            ApproveOverride = (_, _, _) => Task.FromException(new AuthRequestOperationException("批准失败"))
        };
        fake.AddRequest(new PendingAuthRequest("r1", "Android", "1.1.1.1", "2025-01-01T00:00:00Z", "pk"));
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));

        await vm.RefreshRequestsAsync();
        var item = vm.PendingRequests.Single();

        await vm.ApproveAsync(item, (_, _) => Task.FromResult(true));

        Assert.NotNull(vm.Error);
        Assert.Single(vm.PendingRequests); // item 未被移除
        Assert.Equal(DataPageState.Content, vm.RequestsState);
    }

    [Fact]
    public async Task ApproveAsync_NullService_SetsError()
    {
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(null));
        var item = new AuthRequestItem("r1", "Android", "1.1.1.1", "2025/1/1 10:00", "pk");
        vm.PendingRequests.Add(item);

        await vm.ApproveAsync(item, (_, _) => Task.FromResult(true));

        Assert.NotNull(vm.Error);
        Assert.False(string.IsNullOrEmpty(vm.Error));
    }

    [Fact]
    public async Task ApproveAsync_ServiceThrowsOperationCanceled_ReturnsCancelledAndPreservesContent()
    {
        var fake = new FakeAuthRequestService();
        fake.AddRequest(new PendingAuthRequest("r1", "Android", "1.2.3.4", "2025-01-01T10:00:00Z", "pk"));
        fake.ApproveOverride = (_, _, _) => Task.FromException(new OperationCanceledException());

        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));
        await vm.RefreshRequestsAsync();
        var item = vm.PendingRequests.Single();

        var result = await vm.ApproveAsync(item, (_, _) => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Single(vm.PendingRequests);
        Assert.Equal(DataPageState.Content, vm.RequestsState);
        Assert.Null(vm.Error);
    }

    // 用户在指纹确认弹窗点“取消” → 绝不加密下发，条目保留。
    [Fact]
    public async Task ApproveAsync_WhenConfirmationDeclined_DoesNotApprove_AndItemStays()
    {
        var fake = new FakeAuthRequestService();
        fake.AddRequest(new PendingAuthRequest("r1", "Android", "1.2.3.4", "2025-01-01T10:00:00Z", "pk"));
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));
        await vm.RefreshRequestsAsync();
        var item = vm.PendingRequests.Single();

        await vm.ApproveAsync(item, (_, _) => Task.FromResult(false)); // 取消

        Assert.Empty(fake.ApprovedIds);            // 未调用 approve/加密
        Assert.Single(vm.PendingRequests);         // 条目未移除
    }

    // 用户确认指纹一致 → 正常加密下发并移除条目。
    [Fact]
    public async Task ApproveAsync_WhenConfirmed_Approves_AndRemovesItem()
    {
        var fake = new FakeAuthRequestService();
        fake.AddRequest(new PendingAuthRequest("r1", "Android", "1.2.3.4", "2025-01-01T10:00:00Z", "pubkey-abc"));
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));
        await vm.RefreshRequestsAsync();
        var item = vm.PendingRequests.Single();

        await vm.ApproveAsync(item, (_, _) => Task.FromResult(true));

        Assert.Contains("r1", fake.ApprovedIds);
        Assert.Empty(vm.PendingRequests);
    }

    // 确认回调抛出异常（例如弹窗被强制关闭/取消令牌）→ 同样必须走拒绝路径，绝不下发。
    [Fact]
    public async Task ApproveAsync_WhenConfirmationThrowsOperationCanceled_DoesNotApprove_AndItemStays()
    {
        var fake = new FakeAuthRequestService();
        fake.AddRequest(new PendingAuthRequest("r1", "Android", "1.2.3.4", "2025-01-01T10:00:00Z", "pk"));
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));
        await vm.RefreshRequestsAsync();
        var item = vm.PendingRequests.Single();

        await vm.ApproveAsync(item, (_, _) => throw new OperationCanceledException());

        Assert.Empty(fake.ApprovedIds);
        Assert.Single(vm.PendingRequests);
    }

    // ── DenyAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DenyAsync_DelegatesAndRemovesItemFromList()
    {
        var fake = new FakeAuthRequestService();
        fake.AddRequest(new PendingAuthRequest("r2", "iOS", "9.9.9.9", "2025-02-01T15:00:00Z", "pubkey-def"));
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));
        await vm.RefreshRequestsAsync();
        var item = vm.PendingRequests.Single();

        await vm.DenyAsync(item, (_, _) => Task.FromResult(true));

        Assert.Empty(vm.PendingRequests);
        Assert.Contains("r2", fake.DeniedIds);
    }

    [Fact]
    public async Task DenyAsync_ServiceThrows_SetsError_ItemStaysInList()
    {
        var fake = new ThrowingAuthRequestService("拒绝失败");
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));
        var item = new AuthRequestItem("r2", "iOS", "9.9.9.9", "2025/2/1 15:00", "pk");
        vm.PendingRequests.Add(item);

        await vm.DenyAsync(item, (_, _) => Task.FromResult(true));

        Assert.NotNull(vm.Error);
        Assert.Single(vm.PendingRequests);
    }

    [Fact]
    public async Task DenyAsync_NullService_SetsError()
    {
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(null));
        var item = new AuthRequestItem("r2", "iOS", "9.9.9.9", "2025/2/1 15:00", "pk");
        vm.PendingRequests.Add(item);

        await vm.DenyAsync(item, (_, _) => Task.FromResult(true));

        Assert.NotNull(vm.Error);
        Assert.False(string.IsNullOrEmpty(vm.Error));
    }

    [Fact]
    public async Task RefreshRequestsAsync_CancellationRestoresExistingCollectionState()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeAuthRequestService
        {
            ListOverride = async ct =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
                return Array.Empty<PendingAuthRequest>();
            }
        };
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));
        vm.PendingRequests.Add(new AuthRequestItem("r1", "Android", "1.1.1.1", "2025/1/1 10:00", "pk"));

        var cts = new CancellationTokenSource();
        var refreshTask = vm.RefreshRequestsAsync(cts.Token);
        await entered.Task;
        Assert.True(vm.ShowRequestsLoading);

        cts.Cancel();

        var result = await refreshTask;

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Equal(DataPageState.Content, vm.RequestsState);
        Assert.Single(vm.PendingRequests);
        Assert.Null(vm.Error);
    }

    // ── Task 6: Gate / 四态 / Deny 确认 ─────────────────────────────────────

    [Fact]
    public async Task Approve_HoldsGateBeforeConfirmation_ConcurrentDenyReturnsBusy()
    {
        var fake = new FakeAuthRequestService();
        fake.AddRequest(new PendingAuthRequest(
            "r1", "Android", "1.2.3.4", "2025-01-01T10:00:00Z", "pk"));
        var vm = new DevicesViewModel(
            new MockDeviceUiService(),
            new AuthRequestUiService(fake));
        await vm.RefreshRequestsAsync();
        var item = Assert.Single(vm.PendingRequests);
        var confirmation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var first = vm.ApproveAsync(
            item,
            (_, _) => confirmation.Task,
            CancellationToken.None);
        Assert.True(vm.IsBusy);

        var second = await vm.DenyAsync(
            item,
            (_, _) => Task.FromResult(true),
            CancellationToken.None);

        Assert.Equal(UiOperationStatus.Busy, second.Status);
        Assert.Empty(fake.DeniedIds);

        confirmation.SetResult(true);
        Assert.True((await first).IsSuccess);
        Assert.Single(fake.ApprovedIds);
    }

    [Fact]
    public async Task Deny_WhenConfirmationDeclined_DoesNotCallService()
    {
        var fake = new FakeAuthRequestService();
        var vm = new DevicesViewModel(
            new MockDeviceUiService(),
            new AuthRequestUiService(fake));
        var item = new AuthRequestItem(
            "r2", "Windows", "1.1.1.1", "2026/7/10 10:00", "pk");
        vm.PendingRequests.Add(item);

        var result = await vm.DenyAsync(
            item,
            (_, _) => Task.FromResult(false),
            CancellationToken.None);

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Empty(fake.DeniedIds);
        Assert.Contains(item, vm.PendingRequests);
    }

    // ── Finding A: 真实 API 失败 = VaultApiException/VaultWriteException : HttpRequestException ──
    // 这些是网络层抛出的真实类型(见 src/Api/VaultApiException.cs)。它们既不是 OCE 也不是
    // AuthRequestOperationException,当前 catch 覆盖不到,会逃出 async void 页面处理器 → 全局
    // fail-closed 终止进程。修复后必须被精确捕获、映射成固定脱敏文案(不回显服务端 Message)。

    [Fact]
    public async Task RefreshRequestsAsync_ServiceThrowsHttpRequestException_ReturnsFailed_DoesNotThrow_RedactsMessage()
    {
        var fake = new FakeAuthRequestService
        {
            ListOverride = _ => Task.FromException<IReadOnlyList<PendingAuthRequest>>(
                new VaultApiException(HttpStatusCode.BadGateway, "SENSITIVE-SERVER-DETAIL", "SENSITIVE-SERVER-DETAIL")),
        };
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));

        var result = await vm.RefreshRequestsAsync();

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(DataPageState.Error, vm.RequestsState);
        Assert.False(vm.IsBusy);
        Assert.NotNull(vm.Error);
        Assert.False(string.IsNullOrEmpty(vm.Error));
        Assert.DoesNotContain("SENSITIVE-SERVER-DETAIL", vm.Error);
    }

    [Fact]
    public async Task ApproveAsync_ServiceThrowsHttpRequestException_SetsError_ItemStays_DoesNotThrow_RedactsMessage()
    {
        var fake = new FakeAuthRequestService
        {
            ApproveOverride = (_, _, _) => Task.FromException(
                new VaultWriteException("SENSITIVE-SERVER-DETAIL")),
        };
        fake.AddRequest(new PendingAuthRequest("r1", "Android", "1.1.1.1", "2025-01-01T00:00:00Z", "pk"));
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));
        await vm.RefreshRequestsAsync();
        var item = vm.PendingRequests.Single();

        var result = await vm.ApproveAsync(item, (_, _) => Task.FromResult(true));

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.False(vm.IsBusy);
        Assert.NotNull(vm.Error);
        Assert.DoesNotContain("SENSITIVE-SERVER-DETAIL", vm.Error);
        Assert.Single(vm.PendingRequests); // 条目保留,允许重试
        Assert.Equal(DataPageState.Content, vm.RequestsState);
    }

    [Fact]
    public async Task DenyAsync_ServiceThrowsHttpRequestException_SetsError_ItemStays_DoesNotThrow_RedactsMessage()
    {
        var fake = new FakeAuthRequestService
        {
            DenyOverride = (_, _) => Task.FromException(
                new VaultWriteException("SENSITIVE-SERVER-DETAIL")),
        };
        fake.AddRequest(new PendingAuthRequest("r2", "iOS", "9.9.9.9", "2025-02-01T15:00:00Z", "pk"));
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));
        await vm.RefreshRequestsAsync();
        var item = vm.PendingRequests.Single();

        var result = await vm.DenyAsync(item, (_, _) => Task.FromResult(true));

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.False(vm.IsBusy);
        Assert.NotNull(vm.Error);
        Assert.DoesNotContain("SENSITIVE-SERVER-DETAIL", vm.Error);
        Assert.Single(vm.PendingRequests);
        Assert.Equal(DataPageState.Content, vm.RequestsState);
    }

    // 网络失败后闸门必须已释放:紧接着的操作不能返回 Busy。
    [Fact]
    public async Task ApproveAsync_ServiceThrowsHttpRequestException_ReleasesGate_ForNextOperation()
    {
        var fake = new FakeAuthRequestService
        {
            ApproveOverride = (_, _, _) => Task.FromException(
                new VaultApiException(HttpStatusCode.ServiceUnavailable, "srv", "srv")),
        };
        fake.AddRequest(new PendingAuthRequest("r1", "Android", "1.1.1.1", "2025-01-01T00:00:00Z", "pk"));
        var vm = new DevicesViewModel(new MockDeviceUiService(), new AuthRequestUiService(fake));
        await vm.RefreshRequestsAsync();
        var item = vm.PendingRequests.Single();

        await vm.ApproveAsync(item, (_, _) => Task.FromResult(true));
        var second = await vm.DenyAsync(item, (_, _) => Task.FromResult(false));

        Assert.NotEqual(UiOperationStatus.Busy, second.Status);
    }

    // ── 私有 Stub / Fake 类 ───────────────────────────────────────────────────

    private sealed class EmptyDeviceUiService : IDeviceUiService
    {
        public IReadOnlyList<DeviceItem> GetDevices() => Array.Empty<DeviceItem>();
    }

    /// <summary>可控的 FakeAuthRequestService，支持记录调用参数。</summary>
    private sealed class FakeAuthRequestService : IAuthRequestService
    {
        private readonly List<PendingAuthRequest> _requests = new();
        public List<string> ApprovedIds { get; } = new();
        public Dictionary<string, string> ApprovedPublicKeys { get; } = new();
        public List<string> DeniedIds { get; } = new();
        public Func<CancellationToken, Task<IReadOnlyList<PendingAuthRequest>>>? ListOverride { get; set; }
        public Func<string, string, CancellationToken, Task>? ApproveOverride { get; set; }
        public Func<string, CancellationToken, Task>? DenyOverride { get; set; }

        public void AddRequest(PendingAuthRequest r) => _requests.Add(r);
        public void Clear() => _requests.Clear();

        public Task<IReadOnlyList<PendingAuthRequest>> ListPendingAsync(CancellationToken ct = default) =>
            ListOverride?.Invoke(ct) ?? Task.FromResult<IReadOnlyList<PendingAuthRequest>>(_requests.ToList());

        public Task ApproveAsync(string id, string publicKey, CancellationToken ct = default)
        {
            if (ApproveOverride is not null)
                return ApproveOverride(id, publicKey, ct);
            ApprovedIds.Add(id);
            ApprovedPublicKeys[id] = publicKey;
            return Task.CompletedTask;
        }

        public Task DenyAsync(string id, CancellationToken ct = default)
        {
            if (DenyOverride is not null)
                return DenyOverride(id, ct);
            DeniedIds.Add(id);
            return Task.CompletedTask;
        }
    }

    /// <summary>抛出 AuthRequestOperationException 的服务，用于测试错误路径。</summary>
    private sealed class ThrowingAuthRequestService : IAuthRequestService
    {
        private readonly string _message;
        public ThrowingAuthRequestService(string message) => _message = message;

        public Task<IReadOnlyList<PendingAuthRequest>> ListPendingAsync(CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<PendingAuthRequest>>(new AuthRequestOperationException(_message));

        public Task ApproveAsync(string id, string publicKey, CancellationToken ct = default) =>
            Task.FromException(new AuthRequestOperationException(_message));

        public Task DenyAsync(string id, CancellationToken ct = default) =>
            Task.FromException(new AuthRequestOperationException(_message));
    }
}
