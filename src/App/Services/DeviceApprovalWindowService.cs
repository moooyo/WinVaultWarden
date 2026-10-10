using App.ViewModels.Models;
using Api;
using App.Views;
using Core.Services;
using Core.Session;
using Vault;

namespace App.Services;

/// <summary>Shows real server requests in a single independent window and preserves dismissed requests.</summary>
public sealed class DeviceApprovalWindowService(
    IAuthRequestUiService requests,
    VaultSession session,
    ITransientOperationRegistry transients,
    IAppLifecycleCoordinator lifecycle) : ISensitiveState
{
    private readonly DeviceApprovalDecisionService _decisions = new(requests, session);
    private readonly object _stateGate = new();
    private readonly HashSet<string> _dismissed = [];
    private IReadOnlyList<AuthRequestItem> _pending = [];
    private VaultSessionLease? _pendingLease;
    private DeviceApprovalWindow? _window;
    private bool _refreshing;
    private bool _refreshAgain;

    public Func<CancellationToken, Task<bool>>? UnlockAsync { get; set; }
    public event Action<string>? NoticeRaised;
    public event Action? PendingRequestsChanged;

    public int PendingCount
    {
        get
        {
            VaultSessionLease? lease;
            int count;
            lock (_stateGate)
            {
                lease = _pendingLease;
                count = _pending.Count(_decisions.IsPending);
            }
            return lease is not null && session.IsUnlocked(lease) ? count : 0;
        }
    }

    public async Task ShowPendingRequestsAsync(bool reopenDismissed = false)
    {
        if (session.IsDemo || session.State != VaultState.Unlocked)
            return;
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }
        _refreshing = true;
        try
        {
            var lease = session.CaptureLease();
            var items = await requests.ListPendingAsync(lease.CancellationToken);
            var committed = lifecycle.TryRunSensitive(() => session.TryRunIfUnlocked(lease, () =>
            {
                lock (_stateGate)
                {
                    if (_pendingLease?.SameSessionAs(lease) != true)
                        _dismissed.Clear();
                    _pending = items.Where(_decisions.IsPending).ToArray();
                    _pendingLease = lease;
                    _dismissed.RemoveWhere(id => !_pending.Any(item => item.Id == id));
                    if (reopenDismissed)
                        _dismissed.Clear();
                    if (_window is { } open && !_pending.Any(item => item.Id == open.RequestId))
                        open.CancelAndClose();
                    if (_window is { } active)
                        active.QueuedRequestCount = _pending.Count(item => item.Id != active.RequestId && !_dismissed.Contains(item.Id));
                }
            }));
            if (committed)
            {
                ShowNext();
                PendingRequestsChanged?.Invoke();
            }
        }
        catch (OperationCanceledException ex) when (UiExceptionPolicy.IsGenuineCancellation(ex))
        {
            // The originating session was cancelled while loading pending requests.
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            if (reopenDismissed)
                NoticeRaised?.Invoke(UiExceptionPolicy.RequestTimeoutMessage);
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthRequestOperationException
            or InvalidOperationException)
        {
            if (reopenDismissed)
                NoticeRaised?.Invoke("无法加载登录请求，请先解锁密码库并检查网络连接。");
        }
        finally
        {
            _refreshing = false;
            if (_refreshAgain)
            {
                _refreshAgain = false;
                await ShowPendingRequestsAsync(reopenDismissed);
            }
        }
    }

    public void OpenRequest(AuthRequestItem item)
        => _ = OpenFreshRequestAsync(item);

    private async Task OpenFreshRequestAsync(AuthRequestItem item)
    {
        if (session.IsDemo || session.State != VaultState.Unlocked || !_decisions.IsPending(item))
        {
            NoticeRaised?.Invoke("登录请求已过期或当前账户不可用。");
            return;
        }
        try
        {
            var lease = session.CaptureLease();
            var fresh = (await requests.ListPendingAsync(lease.CancellationToken))
                .FirstOrDefault(request => request.Id == item.Id && _decisions.IsPending(request));
            if (fresh is not null)
                OpenRequestFromLease(fresh, lease);
            else if (session.IsCurrent(lease))
                NoticeRaised?.Invoke("登录请求已处理或过期。");
        }
        catch (OperationCanceledException ex) when (UiExceptionPolicy.IsGenuineCancellation(ex))
        {
            // A stale settings row must not display a request from an earlier account.
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            NoticeRaised?.Invoke(UiExceptionPolicy.RequestTimeoutMessage);
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthRequestOperationException or InvalidOperationException)
        {
            NoticeRaised?.Invoke("无法加载登录请求，请检查连接后重试。");
        }
    }

    private void OpenRequestFromLease(AuthRequestItem item, VaultSessionLease lease)
    {
        if (!_decisions.IsPending(item))
            return;
        lifecycle.TryRunSensitive(() => session.TryRunIfUnlocked(lease, () =>
        {
            lock (_stateGate)
                OpenRequestCore(item, lease);
        }));
    }

    private void OpenRequestCore(AuthRequestItem item, VaultSessionLease lease)
    {
        if (_window is { } existing)
        {
            if (existing.RequestId == item.Id)
            {
                existing.Activate();
                return;
            }
            existing.CancelAndClose();
        }
        _dismissed.Remove(item.Id);
        var window = new DeviceApprovalWindow(item, session, lease, ApproveAsync, DenyAsync);
        window.QueuedRequestCount = _pending.Count(request => request.Id != item.Id && !_dismissed.Contains(request.Id));
        _window = window;
        var tracking = transients.Register(window.CancelOperations, window.CancelAndClose, window.ClearSensitive);
        window.Closed += (_, _) =>
        {
            tracking.Dispose();
            lock (_stateGate)
            {
                if (ReferenceEquals(_window, window))
                    _window = null;
            }
            if (window.Outcome == DeviceApprovalOutcome.Cancelled)
                return;
            if (window.Outcome == DeviceApprovalOutcome.Deferred)
            {
                lock (_stateGate)
                    _dismissed.Add(item.Id);
                NoticeRaised?.Invoke("登录请求还在等待，可以在“设置 › 设备”里处理。");
            }
            else if (window.Outcome == DeviceApprovalOutcome.Expired)
            {
                lock (_stateGate)
                    _pending = _pending.Where(request => request.Id != item.Id).ToArray();
                NoticeRaised?.Invoke($"{item.DeviceTypeName} 的登录请求已过期。");
            }
            PendingRequestsChanged?.Invoke();
            if (window.Outcome != DeviceApprovalOutcome.Cancelled)
                window.DispatcherQueue.TryEnqueue(ShowNext);
        };
        // Show without activation so a terminal keystroke cannot approve a new request.
        window.AppWindow.Show(false);
    }

    private void ShowNext()
    {
        AuthRequestItem? next;
        VaultSessionLease? lease;
        lock (_stateGate)
        {
            if (_window is not null)
                return;
            next = _pending.Where(item => _decisions.IsPending(item) && !_dismissed.Contains(item.Id))
                .OrderByDescending(item => item.CreatedAt).FirstOrDefault();
            lease = _pendingLease;
        }
        if (next is not null && lease is not null)
            OpenRequestFromLease(next, lease);
    }

    private async Task<UiOperationResult> ApproveAsync(AuthRequestItem item, bool reviewed, CancellationToken ct)
    {
        var result = await _decisions.ApproveAsync(item, reviewed,
            token => UnlockAsync?.Invoke(token) ?? Task.FromResult(false), ct);
        if (result.IsSuccess)
        {
            lock (_stateGate)
                _pending = _pending.Where(request => request.Id != item.Id).ToArray();
            NoticeRaised?.Invoke($"已批准 {item.DeviceTypeName} 登录。");
        }
        return result;
    }

    private async Task<UiOperationResult> DenyAsync(AuthRequestItem item, CancellationToken ct)
    {
        try
        {
            var lease = session.CaptureLease();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
            await requests.DenyAsync(item.Id, linked.Token);
            if (!session.IsCurrent(lease))
                return UiOperationResult.Cancelled();
            lock (_stateGate)
                _pending = _pending.Where(request => request.Id != item.Id).ToArray();
            NoticeRaised?.Invoke($"已拒绝 {item.DeviceTypeName} 的登录请求。");
            return UiOperationResult.Success();
        }
        catch (OperationCanceledException ex) when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            return UiOperationResult.Failure(UiExceptionPolicy.RequestTimeoutMessage);
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthRequestOperationException or InvalidOperationException)
        {
            return UiOperationResult.Failure("无法拒绝登录请求，请检查网络连接后重试。");
        }
    }

    public void ClearSensitive()
    {
        DeviceApprovalWindow? window;
        lock (_stateGate)
        {
            _pending = [];
            _pendingLease = null;
            _dismissed.Clear();
            window = _window;
            _window = null;
        }
        window?.CancelAndClose();
    }
}
