using App.Views;
using Core.Session;
using Microsoft.UI.Dispatching;
using Vault;

namespace App.Services;

/// <summary>Owns metadata-only locked windows independently of unlocked approval windows.</summary>
public sealed class LockedDeviceRequestWindowService : ISensitiveState, IDisposable
{
    private readonly LockedDeviceRequestMonitor _monitor;
    private readonly DeviceApprovalWindowService _unlockedWindows;
    private readonly VaultSession _session;
    private readonly IAppLifecycleCoordinator _lifecycle;
    private readonly ITransientOperationRegistry _transients;
    private readonly DispatcherQueue _dispatcher;
    private readonly HashSet<(Guid Epoch, string Id)> _dismissed = [];
    private LockedDeviceRequestWindow? _window;
    private bool _disposed;

    public LockedDeviceRequestWindowService(
        LockedDeviceRequestMonitor monitor,
        DeviceApprovalWindowService unlockedWindows,
        VaultSession session,
        IAppLifecycleCoordinator lifecycle,
        ITransientOperationRegistry transients)
    {
        _monitor = monitor;
        _unlockedWindows = unlockedWindows;
        _session = session;
        _lifecycle = lifecycle;
        _transients = transients;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Locked device request windows require the UI thread.");
        _monitor.NoticesChanged += OnNoticesChanged;
        _monitor.UnlockRequested += OnUnlockRequested;
    }

    /// <summary>The host restores its real sign-in/unlock UI for this exact account.</summary>
    public Action<LockedDeviceRequestAccount>? UnlockRequested { get; set; }
    public event Action<string>? NoticeRaised;

    /// <summary>Call after ShowVault succeeds; metadata intent is consumed before replacing its credential scope.</summary>
    public async Task OnUnlockedAsync(VaultSessionLease lease, bool explicitlyEnabled)
    {
        if (_disposed || !explicitlyEnabled)
        {
            Stop();
            return;
        }
        try
        {
            var review = await _monitor.ResumeAfterUnlockAsync(lease, lease.CancellationToken);
            if (_session.IsUnlocked(lease))
            {
                if (review.IsSuccess && review.Value is { } item)
                    _unlockedWindows.OpenRevalidatedRequest(item, lease);
                else if (review.Status == UiOperationStatus.Failed)
                    NoticeRaised?.Invoke(review.Error ?? "The device request could not be verified.");
                if (_monitor.ArmFromUnlocked(lease, explicitlyEnabled))
                    _monitor.StartPolling();
            }
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Stop(); }
    }

    /// <summary>Call after an ordinary lock transition completes, including minimize-to-lock and tray hiding.</summary>
    public Task OnLockedAsync() => _monitor.RefreshAsync();

    private void OnNoticesChanged() => _dispatcher.TryEnqueue(Present);

    private void OnUnlockRequested(LockedDeviceRequestAccount account)
    {
        var unlock = UnlockRequested
            ?? throw new InvalidOperationException("The host has not configured its real account unlock UI.");
        if (_dispatcher.HasThreadAccess)
            unlock(account);
        else if (!_dispatcher.TryEnqueue(() => unlock(account)))
            throw new InvalidOperationException("The real account unlock UI is unavailable.");
    }

    private void Present()
    {
        if (_disposed || !_monitor.IsActive || _session.State != VaultState.Locked || _lifecycle.IsTransitioning)
        {
            if (!_monitor.IsActive) _dismissed.Clear();
            CloseWindow();
            return;
        }
        var notices = _monitor.Notices;
        if (_window is { } open)
        {
            if (notices.Any(open.Represents))
                return;
            CloseWindow();
        }
        var next = notices.FirstOrDefault(notice => notice.Request.ExpiresAt > DateTimeOffset.UtcNow
            && !_dismissed.Contains((notice.Epoch, notice.Request.Id)));
        if (next is null)
            return;
        var window = new LockedDeviceRequestWindow(next, () => _monitor.RequestUnlock(next));
        if (window.IsClosed)
            return;
        _window = window;
        var tracking = _transients.Register(window.CancelAndClose, window.CancelAndClose, window.ClearSensitive);
        window.Closed += (_, _) =>
        {
            tracking.Dispose();
            if (ReferenceEquals(_window, window))
                _window = null;
            if (!window.WasCancelled)
                _dismissed.Add((next.Epoch, next.Request.Id));
            _dispatcher.TryEnqueue(Present);
        };
        window.AppWindow.Show(false);
    }

    private void CloseWindow()
    {
        var window = _window;
        _window = null;
        window?.CancelAndClose();
    }

    public void ClearSensitive()
    {
        _monitor.ClearSensitive();
        void ClearOnUiThread() { _dismissed.Clear(); CloseWindow(); }
        if (_dispatcher.HasThreadAccess) ClearOnUiThread();
        else _dispatcher.TryEnqueue(ClearOnUiThread);
    }

    /// <summary>Required before opt-out, logout, remote logout, credential change, account switch or exit.</summary>
    public void Stop()
    {
        _monitor.Stop();
        ClearSensitive();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _monitor.NoticesChanged -= OnNoticesChanged;
        _monitor.UnlockRequested -= OnUnlockRequested;
    }
}
