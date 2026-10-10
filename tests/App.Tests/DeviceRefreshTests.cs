using Api;
using Api.Dtos;
using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Core.Models;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace App.Tests;

public sealed class DeviceRefreshTests
{
    [Theory]
    [InlineData("native-device")]
    [InlineData("SERVER-ID")]
    public async Task ServiceRefresh_MapsServerMetadataAndCurrentDevice(string currentIdentifier)
    {
        using var context = new SessionContext();
        var created = new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero);
        var api = new ControlledReadonlyClient
        {
            Response = Response(
                new DeviceDto("server-id", "Workstation", 6, "NATIVE-DEVICE", created, true),
                new DeviceDto("phone-id", "Phone", 8, "other-device", null, false)),
        };
        var provider = new RecordingProvider(api);
        var service = new DeviceUiService(context.Vault, currentIdentifier, provider, context.Session);

        var result = await service.RefreshAsync();

        Assert.Equal(1, api.Calls);
        Assert.NotNull(provider.Lease);
        Assert.True(context.Session.CaptureLease().SameSessionAs(provider.Lease));
        Assert.Collection(context.Session.Devices,
            device => Assert.Equal(new DeviceInfo("server-id", "Workstation", 6, "NATIVE-DEVICE", created, true), device),
            device => Assert.Equal(new DeviceInfo("phone-id", "Phone", 8, "other-device", null, false), device));
        Assert.Collection(result,
            device =>
            {
                Assert.Equal("server-id", device.Id);
                Assert.Equal("Workstation", device.Name);
                Assert.True(device.IsCurrent);
                Assert.True(device.IsTrusted);
                Assert.Contains(created.LocalDateTime.ToString("yyyy/M/d HH:mm"), device.RegisteredLabel);
                Assert.NotEmpty(device.Glyph);
            },
            device =>
            {
                Assert.Equal("phone-id", device.Id);
                Assert.False(device.IsCurrent);
                Assert.False(device.IsTrusted);
                Assert.NotEqual(result[0].Glyph, device.Glyph);
            });
    }

    [Fact]
    public async Task ServiceRefresh_EmptyResponseReplacesPreviousSnapshot()
    {
        using var context = new SessionContext();
        var api = new ControlledReadonlyClient();

        var result = await CreateService(context, api).RefreshAsync();

        Assert.Empty(result);
        Assert.Empty(context.Session.Devices);
    }

    [Fact]
    public async Task ServiceRefresh_NetworkFailurePreservesPreviousSnapshot()
    {
        using var context = new SessionContext();
        var previous = context.Session.Devices;
        var failure = new HttpRequestException("SERVER-PRIVATE-DETAIL");
        var api = new ControlledReadonlyClient { Failure = failure };

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateService(context, api).RefreshAsync());

        Assert.Same(failure, thrown);
        Assert.Same(previous, context.Session.Devices);
    }

    [Fact]
    public async Task ServiceRefresh_PreCancelledRequestDoesNotReachProvider()
    {
        using var context = new SessionContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var previous = context.Session.Devices;
        var api = new ControlledReadonlyClient();
        var provider = new RecordingProvider(api);
        var service = new DeviceUiService(context.Vault, "device-a", provider, context.Session);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RefreshAsync(cancellation.Token));

        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, api.Calls);
        Assert.Same(previous, context.Session.Devices);
    }

    [Fact]
    public async Task ServiceRefresh_CallerCancellationPreventsCommitWhenClientIgnoresToken()
    {
        using var context = new SessionContext();
        using var cancellation = new CancellationTokenSource();
        var previous = context.Session.Devices;
        var api = new ControlledReadonlyClient { Defer = true };
        var refresh = CreateService(context, api).RefreshAsync(cancellation.Token);
        Assert.Equal(1, api.Calls);

        cancellation.Cancel();
        api.Complete(Response(NewDevice()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.True(api.LastToken.IsCancellationRequested);
        Assert.Same(previous, context.Session.Devices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceRefresh_SessionChangePreventsStaleCommitWhenClientIgnoresToken(bool switchAccount)
    {
        using var context = new SessionContext();
        var api = new ControlledReadonlyClient { Defer = true };
        var refresh = CreateService(context, api).RefreshAsync();
        Assert.Equal(1, api.Calls);

        context.ChangeSession(switchAccount);
        var currentSnapshot = context.Session.Devices;
        api.Complete(Response(NewDevice()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.True(api.LastToken.IsCancellationRequested);
        Assert.Same(currentSnapshot, context.Session.Devices);
        Assert.DoesNotContain(context.Session.Devices, device => device.Id == "refreshed-device");
        Assert.Equal(switchAccount ? VaultState.Unlocked : VaultState.Locked, context.Session.State);
    }

    [Fact]
    public async Task ServiceRefresh_LockedSessionDoesNotReachProvider()
    {
        using var context = new SessionContext();
        context.Session.Lock();
        var api = new ControlledReadonlyClient();
        var provider = new RecordingProvider(api);
        var service = new DeviceUiService(context.Vault, "device-a", provider, context.Session);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshAsync());

        Assert.Equal(0, provider.Calls);
        Assert.Empty(context.Session.Devices);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ServiceRefresh_MissingOptionalDependenciesReturnsSnapshotWithoutNetwork(bool hasProvider, bool hasSession)
    {
        using var context = new SessionContext();
        var previous = context.Session.Devices;
        var api = new ControlledReadonlyClient { Failure = new InvalidOperationException("Unexpected network call.") };
        var provider = new RecordingProvider(api);
        var service = new DeviceUiService(context.Vault, "device-a",
            hasProvider ? provider : null, hasSession ? context.Session : null);

        var result = await service.RefreshAsync();

        Assert.Equal("device-a", Assert.Single(result).Id);
        Assert.True(result[0].IsCurrent);
        Assert.Same(previous, context.Session.Devices);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ServiceRefresh_DemoSessionReturnsSnapshotWithoutNetwork()
    {
        using var context = new SessionContext();
        context.Session.MarkDemo();
        var api = new ControlledReadonlyClient { Failure = new InvalidOperationException("Unexpected network call.") };

        var result = await CreateService(context, api).RefreshAsync();

        Assert.Equal("device-a", Assert.Single(result).Id);
        Assert.Equal(0, api.Calls);
    }

    [Fact]
    public async Task DefaultInterfaceRefresh_ReturnsLegacyServiceSnapshot()
    {
        IDeviceUiService service = new SnapshotOnlyService();

        var result = await service.RefreshAsync();

        Assert.Equal("original", Assert.Single(result).Id);
    }

    [Fact]
    public async Task ViewModelRefresh_ReplacesObservableListAndUpdatesEmptyNotification()
    {
        var service = new ControlledDeviceUiService { Result = [NewItem()] };
        var viewModel = new DevicesViewModel(service);
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        var collection = viewModel.Devices;

        var refreshed = await viewModel.RefreshDevicesAsync();

        Assert.True(refreshed.IsSuccess);
        Assert.Same(collection, viewModel.Devices);
        Assert.Equal("refreshed-device", Assert.Single(viewModel.Devices).Id);
        Assert.False(viewModel.HasNoDevices);
        Assert.Equal(1, service.SnapshotCalls);
        Assert.Equal(1, service.RefreshCalls);
        Assert.Contains(nameof(DevicesViewModel.HasNoDevices), notifications);

        service.Result = [];
        var empty = await viewModel.RefreshDevicesAsync();

        Assert.True(empty.IsSuccess);
        Assert.Empty(viewModel.Devices);
        Assert.True(viewModel.HasNoDevices);
    }

    [Fact]
    public async Task ViewModelRefresh_FailureIsRedactedAndSeparateFromRequestErrorThenRetryRecovers()
    {
        var service = new ControlledDeviceUiService { Failure = new HttpRequestException("SERVER-PRIVATE-DETAIL") };
        var viewModel = new DevicesViewModel(service)
        {
            Error = "Existing request error",
            RequestsState = DataPageState.Error,
        };
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        var failed = await viewModel.RefreshDevicesAsync();

        Assert.Equal(UiOperationStatus.Failed, failed.Status);
        Assert.True(viewModel.HasDeviceError);
        Assert.NotNull(viewModel.DeviceError);
        Assert.Equal(viewModel.DeviceError, failed.Error);
        Assert.DoesNotContain("SERVER-PRIVATE-DETAIL", viewModel.DeviceError);
        Assert.Equal("original", Assert.Single(viewModel.Devices).Id);
        Assert.Equal("Existing request error", viewModel.Error);
        Assert.Equal(DataPageState.Error, viewModel.RequestsState);
        Assert.Contains(nameof(DevicesViewModel.HasDeviceError), notifications);
        Assert.False(viewModel.IsDevicesBusy);

        service.Failure = null;
        service.Result = [NewItem()];
        var recovered = await viewModel.RefreshDevicesAsync();

        Assert.True(recovered.IsSuccess);
        Assert.False(viewModel.HasDeviceError);
        Assert.Null(viewModel.DeviceError);
        Assert.Equal("refreshed-device", Assert.Single(viewModel.Devices).Id);
        Assert.Equal("Existing request error", viewModel.Error);
    }

    [Fact]
    public async Task ViewModelRefresh_RequestTimeoutIsFailureAndPreservesPreviousList()
    {
        var service = new ControlledDeviceUiService { Failure = RequestTimeout() };
        var viewModel = new DevicesViewModel(service);

        var result = await viewModel.RefreshDevicesAsync();

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(UiExceptionPolicy.RequestTimeoutMessage, viewModel.DeviceError);
        Assert.Equal("original", Assert.Single(viewModel.Devices).Id);
        Assert.False(viewModel.IsDevicesBusy);
        Assert.Null(viewModel.Error);
    }

    [Fact]
    public async Task ViewModelRefresh_PreCancelledRequestDoesNotInvokeService()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new ControlledDeviceUiService();
        var viewModel = new DevicesViewModel(service);

        var result = await viewModel.RefreshDevicesAsync(cancellation.Token);

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Equal(0, service.RefreshCalls);
        Assert.Equal("original", Assert.Single(viewModel.Devices).Id);
        Assert.Null(viewModel.DeviceError);
        Assert.False(viewModel.IsDevicesBusy);
    }

    [Fact]
    public async Task ViewModelRefresh_CallerCancellationPreventsPublishWhenServiceIgnoresToken()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new ControlledDeviceUiService { Defer = true };
        var viewModel = new DevicesViewModel(service);
        var refresh = viewModel.RefreshDevicesAsync(cancellation.Token);
        Assert.Equal(1, service.RefreshCalls);

        cancellation.Cancel();
        service.Complete([NewItem()]);
        var result = await refresh;

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.True(service.LastToken.IsCancellationRequested);
        Assert.Equal("original", Assert.Single(viewModel.Devices).Id);
        Assert.Null(viewModel.DeviceError);
        Assert.False(viewModel.IsDevicesBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViewModelRefresh_SessionChangePreventsStalePublishWhenServiceIgnoresToken(bool switchAccount)
    {
        using var context = new SessionContext();
        var service = new ControlledDeviceUiService { Defer = true };
        var viewModel = new DevicesViewModel(service, session: context.Session);
        var refresh = viewModel.RefreshDevicesAsync();
        Assert.Equal(1, service.RefreshCalls);

        context.ChangeSession(switchAccount);
        viewModel.ClearSensitive();
        service.Complete([NewItem()]);
        var result = await refresh;

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.True(service.LastToken.IsCancellationRequested);
        Assert.Empty(viewModel.Devices);
        Assert.Null(viewModel.DeviceError);
        Assert.False(viewModel.IsDevicesBusy);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ViewModelRefresh_SessionChangePreventsStaleErrorPublish(bool switchAccount, bool timeout)
    {
        using var context = new SessionContext();
        var service = new ControlledDeviceUiService { Defer = true };
        var viewModel = new DevicesViewModel(service, session: context.Session);
        var refresh = viewModel.RefreshDevicesAsync();
        Assert.Equal(1, service.RefreshCalls);

        context.ChangeSession(switchAccount);
        viewModel.ClearSensitive();
        service.Fail(timeout ? RequestTimeout() : new HttpRequestException("SERVER-PRIVATE-DETAIL"));
        var result = await refresh;

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Empty(viewModel.Devices);
        Assert.Null(viewModel.DeviceError);
        Assert.False(viewModel.HasDeviceError);
    }

    [Fact]
    public async Task ViewModelRefresh_LifecycleTransitionPreventsPublish()
    {
        using var context = new SessionContext();
        var service = new ControlledDeviceUiService { Defer = true };
        var lifecycle = new ControllableLifecycle();
        var viewModel = new DevicesViewModel(service, session: context.Session, lifecycle: lifecycle);
        var refresh = viewModel.RefreshDevicesAsync();
        Assert.Equal(1, service.RefreshCalls);

        lifecycle.IsTransitioning = true;
        viewModel.ClearSensitive();
        service.Complete([NewItem()]);
        var result = await refresh;

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Empty(viewModel.Devices);
        Assert.Null(viewModel.DeviceError);
        Assert.True(lifecycle.SensitiveCalls > 0);
    }

    [Fact]
    public async Task ViewModelRefresh_ConcurrentRefreshReturnsBusyAndReleasesGate()
    {
        var service = new ControlledDeviceUiService { Defer = true };
        var viewModel = new DevicesViewModel(service);
        var first = viewModel.RefreshDevicesAsync();

        Assert.True(viewModel.IsDevicesBusy);
        Assert.False(viewModel.CanOperate);
        var second = await viewModel.RefreshDevicesAsync();
        Assert.Equal(UiOperationStatus.Busy, second.Status);
        Assert.Equal(1, service.RefreshCalls);

        service.Complete([NewItem()]);
        Assert.True((await first).IsSuccess);
        Assert.False(viewModel.IsDevicesBusy);
        Assert.True(viewModel.CanOperate);
        service.Defer = false;
        Assert.True((await viewModel.RefreshDevicesAsync()).IsSuccess);
        Assert.Equal(2, service.RefreshCalls);
    }

    private static DeviceUiService CreateService(SessionContext context, ControlledReadonlyClient api) =>
        new(context.Vault, "device-a", new RecordingProvider(api), context.Session);

    private static ListResponse<DeviceDto> Response(params DeviceDto[] devices) =>
        new(devices, null, "list");

    private static DeviceDto NewDevice() =>
        new("refreshed-device", "New browser", 10, "browser-device", null, false);

    private static DeviceItem NewItem() =>
        new("refreshed-device", "New browser", "browser", "registered", false);

    private static DeviceItem OriginalItem() =>
        new("original", "Existing desktop", "desktop", "registered", true);

    private static TaskCanceledException RequestTimeout() => new(
        "The request timed out.",
        new RequestTimeoutException(RequestTimeoutPhase.ControlPlane, TimeSpan.FromSeconds(30)),
        CancellationToken.None);

    private sealed class SessionContext : IDisposable
    {
        public VaultSession Session { get; } = new();
        public VaultService Vault { get; }

        public SessionContext()
        {
            Session.BeginAccountSession("account-a", "a@example.test", new ServerOrigin("https://a.example.test"));
            Session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
            Session.SetDevices([new DeviceInfo("device-a", "Existing desktop", 6, "device-a", null, false)]);
            Vault = new VaultService(Session);
        }

        public void ChangeSession(bool switchAccount)
        {
            if (!switchAccount)
            {
                Session.Lock();
                return;
            }

            Session.BeginAccountSession("account-b", "b@example.test", new ServerOrigin("https://b.example.test"));
            Session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
            Session.SetDevices([new DeviceInfo("device-b", "Other account desktop", 6, "device-b", null, true)]);
        }

        public void Dispose() => Session.Logout();
    }

    private sealed class RecordingProvider(IReadonlyApiClient client) : IVaultApiClientProvider
    {
        public int Calls { get; private set; }
        public VaultSessionLease? Lease { get; private set; }

        public TClient GetAnonymous<TClient>(ServerOrigin origin) where TClient : class, IAnonymousApiRole =>
            throw new InvalidOperationException("Anonymous access is not expected for device refresh.");

        public TClient GetAuthenticated<TClient>(VaultSessionLease lease) where TClient : class, IAuthenticatedApiRole
        {
            Assert.Equal(typeof(IReadonlyApiClient), typeof(TClient));
            Calls++;
            Lease = lease;
            return (TClient)(object)client;
        }
    }

    private sealed class ControlledReadonlyClient : IReadonlyApiClient
    {
        private readonly TaskCompletionSource<ListResponse<DeviceDto>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Defer { get; init; }
        public Exception? Failure { get; init; }
        public ListResponse<DeviceDto> Response { get; init; } = new([], null, "list");
        public int Calls { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<ListResponse<DeviceDto>> GetDevicesAsync(CancellationToken ct = default)
        {
            Calls++;
            LastToken = ct;
            if (Defer)
                return _completion.Task;
            return Failure is { } failure
                ? Task.FromException<ListResponse<DeviceDto>>(failure)
                : Task.FromResult(Response);
        }

        public void Complete(ListResponse<DeviceDto> response) => _completion.SetResult(response);
        public Task<SyncResponse> GetSyncAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<FolderDto> GetFolderAsync(string folderId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SendResponseDto> GetSendAsync(string sendId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class SnapshotOnlyService : IDeviceUiService
    {
        public IReadOnlyList<DeviceItem> GetDevices() => [OriginalItem()];
    }

    private sealed class ControlledDeviceUiService : IDeviceUiService
    {
        private readonly TaskCompletionSource<IReadOnlyList<DeviceItem>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Defer { get; set; }
        public Exception? Failure { get; set; }
        public IReadOnlyList<DeviceItem> Result { get; set; } = [];
        public int SnapshotCalls { get; private set; }
        public int RefreshCalls { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public IReadOnlyList<DeviceItem> GetDevices()
        {
            SnapshotCalls++;
            return [OriginalItem()];
        }

        public Task<IReadOnlyList<DeviceItem>> RefreshAsync(CancellationToken ct = default)
        {
            RefreshCalls++;
            LastToken = ct;
            if (Defer)
                return _completion.Task;
            return Failure is { } failure
                ? Task.FromException<IReadOnlyList<DeviceItem>>(failure)
                : Task.FromResult(Result);
        }

        public void Complete(IReadOnlyList<DeviceItem> items) => _completion.SetResult(items);
        public void Fail(Exception exception) => _completion.SetException(exception);
    }

    private sealed class ControllableLifecycle : IAppLifecycleCoordinator
    {
        public bool IsTransitioning { get; set; }
        public int SensitiveCalls { get; private set; }

        public bool TryRunSensitive(Func<bool> action)
        {
            SensitiveCalls++;
            return !IsTransitioning && action();
        }

        public Task RequestAsync(AppLifecycleIntent intent, VaultSessionLease? expectedSession = null) =>
            Task.CompletedTask;
        public Task<bool> RequestIfAsync(AppLifecycleIntent intent, Func<bool> predicate) =>
            Task.FromResult(predicate());
        public bool MarkSessionReady() => !IsTransitioning;
        public void FailClosedSynchronously() => IsTransitioning = true;
    }
}
