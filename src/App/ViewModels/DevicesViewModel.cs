using System.Collections.ObjectModel;
using Api;
using App.Services;
using App.ViewModels.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.Services;
using Core.Session;
using Vault;

namespace App.ViewModels;

public partial class DevicesViewModel : ObservableObject, ISensitiveState
{
    private readonly IDeviceUiService _devices;
    private readonly IAuthRequestUiService? _authRequests;
    private readonly VaultSession? _session;
    private readonly IAppLifecycleCoordinator? _lifecycle;
    private readonly AsyncOperationGate _operations = new();
    private readonly AsyncOperationGate _deviceOperations = new();

    // 网络层真实失败为 VaultApiException/VaultWriteException(均 : HttpRequestException)。这些文案
    // 固定脱敏、绝不回显 ex.Message(服务端错误可能夹带账户/资源标识)。
    private const string NetworkLoadError = "无法加载设备登录请求，请检查网络后重试。";
    private const string NetworkOperationError = "网络请求失败，请检查连接后重试。";

    public ObservableCollection<DeviceItem> Devices { get; } = new();
    public ObservableCollection<AuthRequestItem> PendingRequests { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    public partial bool IsDevicesBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceError))]
    public partial string? DeviceError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRequestsLoading))]
    [NotifyPropertyChangedFor(nameof(ShowRequestsError))]
    [NotifyPropertyChangedFor(nameof(ShowRequestsEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowRequestsContent))]
    public partial DataPageState RequestsState { get; set; } = DataPageState.Loading;

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasDeviceError => !string.IsNullOrEmpty(DeviceError);
    public bool HasNoDevices => Devices.Count == 0;
    public bool HasNoPendingRequests => PendingRequests.Count == 0;
    public bool ShowRequestsLoading => RequestsState == DataPageState.Loading;
    public bool ShowRequestsError => RequestsState == DataPageState.Error;
    public bool ShowRequestsEmpty => RequestsState == DataPageState.Empty;
    public bool ShowRequestsContent => RequestsState == DataPageState.Content;
    public bool CanOperate => !IsBusy && !IsDevicesBusy;

    public DevicesViewModel(IDeviceUiService service, IAuthRequestUiService? authRequests = null,
        VaultSession? session = null, IAppLifecycleCoordinator? lifecycle = null)
    {
        _devices = service;
        _authRequests = authRequests;
        _session = session;
        _lifecycle = lifecycle;
        foreach (var d in service.GetDevices()) Devices.Add(d);
        Devices.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoDevices));
        PendingRequests.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoPendingRequests));
    }

    public async Task<UiOperationResult<IReadOnlyList<DeviceItem>>> RefreshDevicesAsync(CancellationToken ct = default)
    {
        if (!_deviceOperations.TryEnter(out var operation))
            return UiOperationResult<IReadOnlyList<DeviceItem>>.Busy();
        using (operation)
        {
            IsDevicesBusy = true;
            DeviceError = null;
            VaultSessionLease? lease = null;
            bool PublishIfCurrent(Action update)
            {
                if (ct.IsCancellationRequested)
                    return false;
                if (_session is null)
                {
                    update();
                    return true;
                }
                if (lease is null)
                    return false;
                return _lifecycle is null ? _session.TryRunIfUnlocked(lease, update)
                    : _lifecycle.TryRunSensitive(() => _session.TryRunIfUnlocked(lease, update));
            }
            try
            {
                ct.ThrowIfCancellationRequested();
                lease = _session?.CaptureLease();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    ct, lease?.CancellationToken ?? CancellationToken.None);
                var items = await _devices.RefreshAsync(linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                var published = false;
                void Publish()
                {
                    if (linked.IsCancellationRequested)
                        return;
                    Devices.Clear();
                    foreach (var item in items)
                        Devices.Add(item);
                    DeviceError = null;
                    published = true;
                }
                PublishIfCurrent(Publish);
                return published ? UiOperationResult<IReadOnlyList<DeviceItem>>.Success(items)
                    : UiOperationResult<IReadOnlyList<DeviceItem>>.Cancelled();
            }
            catch (OperationCanceledException ex) when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
            {
                return UiOperationResult<IReadOnlyList<DeviceItem>>.Cancelled();
            }
            catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
            {
                if (!PublishIfCurrent(() => DeviceError = UiExceptionPolicy.RequestTimeoutMessage))
                    return UiOperationResult<IReadOnlyList<DeviceItem>>.Cancelled();
                return UiOperationResult<IReadOnlyList<DeviceItem>>.Failure(UiExceptionPolicy.RequestTimeoutMessage);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                const string message = "无法刷新已登录的设备，请检查连接并确认密码库已解锁。";
                if (!PublishIfCurrent(() => DeviceError = message))
                    return UiOperationResult<IReadOnlyList<DeviceItem>>.Cancelled();
                return UiOperationResult<IReadOnlyList<DeviceItem>>.Failure(message);
            }
            finally
            {
                IsDevicesBusy = false;
            }
        }
    }

    public void ClearSensitive()
    {
        Devices.Clear();
        PendingRequests.Clear();
        DeviceError = null;
        Error = null;
    }

    // ── 登录授权请求 ──────────────────────────────────────────────────────────

    /// <summary>刷新待审批的设备登录请求列表。</summary>
    public async Task<UiOperationResult<IReadOnlyList<AuthRequestItem>>> RefreshRequestsAsync(
        CancellationToken ct = default)
    {
        if (_session?.IsDemo == true)
        {
            PendingRequests.Clear();
            RequestsState = DataPageState.Empty;
            Error = null;
            return UiOperationResult<IReadOnlyList<AuthRequestItem>>.Success([]);
        }
        if (_authRequests is null)
        {
            const string unavailable = "设备登录授权服务不可用";
            SetLoadFailure(unavailable);
            return UiOperationResult<IReadOnlyList<AuthRequestItem>>.Failure(unavailable);
        }
        if (!_operations.TryEnter(out var operation))
            return UiOperationResult<IReadOnlyList<AuthRequestItem>>.Busy();

        using (operation)
        {
            IsBusy = true;
            RequestsState = DataPageState.Loading;
            Error = null;
            try
            {
                var lease = _session?.CaptureLease();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    ct, lease?.CancellationToken ?? CancellationToken.None);
                var items = await _authRequests.ListPendingAsync(linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                var published = false;
                void Publish()
                {
                    if (linked.IsCancellationRequested)
                        return;
                    PendingRequests.Clear();
                    foreach (var item in items)
                        PendingRequests.Add(item);
                    RequestsState = items.Count == 0 ? DataPageState.Empty : DataPageState.Content;
                    Error = null;
                    published = true;
                }
                if (lease is null)
                    Publish();
                else if (_lifecycle is null)
                    _session!.TryRunIfUnlocked(lease, Publish);
                else
                    _lifecycle.TryRunSensitive(() => _session!.TryRunIfUnlocked(lease, Publish));
                if (!published)
                    return UiOperationResult<IReadOnlyList<AuthRequestItem>>.Cancelled();
                return UiOperationResult<IReadOnlyList<AuthRequestItem>>.Success(items);
            }
            catch (OperationCanceledException ex)
                when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
            {
                RestoreRequestsState();
                return UiOperationResult<IReadOnlyList<AuthRequestItem>>.Cancelled();
            }
            catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
            {
                SetLoadFailure(UiExceptionPolicy.RequestTimeoutMessage);
                return UiOperationResult<IReadOnlyList<AuthRequestItem>>.Failure(
                    UiExceptionPolicy.RequestTimeoutMessage);
            }
            catch (HttpRequestException)
            {
                SetLoadFailure(NetworkLoadError);
                return UiOperationResult<IReadOnlyList<AuthRequestItem>>.Failure(NetworkLoadError);
            }
            catch (AuthRequestOperationException ex)
            {
                SetLoadFailure(ex.Message);
                return UiOperationResult<IReadOnlyList<AuthRequestItem>>.Failure(ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// 批准指定的设备登录请求。审批前在 <paramref name="confirmAsync"/> 中获取用户确认；
    /// Gate 在调用确认前原子持有，第二次并发操作立即返回 Busy。
    /// </summary>
    public async Task<UiOperationResult> ApproveAsync(
        AuthRequestItem item,
        Func<AuthRequestItem, CancellationToken, Task<bool>> confirmAsync,
        CancellationToken ct = default)
    {
        if (_authRequests is null)
            return FailOperation("设备登录授权服务不可用");
        if (!_operations.TryEnter(out var operation))
            return UiOperationResult.Busy();

        using (operation)
        {
            IsBusy = true;
            Error = null;
            try
            {
                bool confirmed;
                try
                {
                    confirmed = await confirmAsync(item, ct);
                }
                catch (OperationCanceledException ex)
                    when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
                {
                    return UiOperationResult.Cancelled();
                }
                if (!confirmed)
                    return UiOperationResult.Cancelled();

                await _authRequests.ApproveAsync(item.Id, item.PublicKey, ct);
                PendingRequests.Remove(item);
                RequestsState = PendingRequests.Count == 0 ? DataPageState.Empty : DataPageState.Content;
                Error = null;
                return UiOperationResult.Success();
            }
            catch (OperationCanceledException ex)
                when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
            {
                return UiOperationResult.Cancelled();
            }
            catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
            {
                return FailOperation(UiExceptionPolicy.RequestTimeoutMessage);
            }
            catch (HttpRequestException)
            {
                return FailOperation(NetworkOperationError);
            }
            catch (AuthRequestOperationException ex)
            {
                return FailOperation(ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// 拒绝指定的设备登录请求。拒绝前要求 <paramref name="confirmAsync"/> 用户确认；
    /// Gate 在调用确认前原子持有，第二次并发操作立即返回 Busy。
    /// </summary>
    public async Task<UiOperationResult> DenyAsync(
        AuthRequestItem item,
        Func<AuthRequestItem, CancellationToken, Task<bool>> confirmAsync,
        CancellationToken ct = default)
    {
        if (_authRequests is null)
            return FailOperation("设备登录授权服务不可用");
        if (!_operations.TryEnter(out var operation))
            return UiOperationResult.Busy();

        using (operation)
        {
            IsBusy = true;
            Error = null;
            try
            {
                if (!await confirmAsync(item, ct))
                    return UiOperationResult.Cancelled();
                await _authRequests.DenyAsync(item.Id, ct);
                PendingRequests.Remove(item);
                RequestsState = PendingRequests.Count == 0 ? DataPageState.Empty : DataPageState.Content;
                Error = null;
                return UiOperationResult.Success();
            }
            catch (OperationCanceledException ex)
                when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
            {
                return UiOperationResult.Cancelled();
            }
            catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
            {
                return FailOperation(UiExceptionPolicy.RequestTimeoutMessage);
            }
            catch (HttpRequestException)
            {
                return FailOperation(NetworkOperationError);
            }
            catch (AuthRequestOperationException ex)
            {
                return FailOperation(ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    private void RestoreRequestsState()
    {
        RequestsState = PendingRequests.Count == 0 ? DataPageState.Empty : DataPageState.Content;
    }

    private void SetLoadFailure(string error)
    {
        Error = error;
        RequestsState = DataPageState.Error;
    }

    private UiOperationResult FailOperation(string error)
    {
        Error = error;
        return UiOperationResult.Failure(error);
    }
}
