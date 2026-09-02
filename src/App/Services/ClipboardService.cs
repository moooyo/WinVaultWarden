using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace App.Services;

/// <summary>
/// Applies the protected clipboard policy to every vault-derived value.
/// An application-owned clipboard format protects external content, while an in-memory write
/// identity prevents an expired timer from clearing a newer application write.
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    private const string OwnershipFormat = "WinVaultWarden.VaultClipboard";

    private readonly object _gate = new();
    private DispatcherQueueTimer? _clearTimer;
    private ClipboardWrite? _latestWrite;

    private sealed class ClipboardWrite { }

    public void SetText(string text) => SetVaultText(text, autoClearSeconds: null);

    public void SetSecretText(string text, int? autoClearSeconds = null) =>
        SetVaultText(text, autoClearSeconds);

    private void SetVaultText(string text, int? autoClearSeconds)
    {
        ArgumentNullException.ThrowIfNull(text);

        var write = new ClipboardWrite();
        var package = new DataPackage();
        package.SetText(text);
        package.SetData(OwnershipFormat, "1");

        lock (_gate)
        {
            Clipboard.SetContentWithOptions(package, new ClipboardContentOptions
            {
                IsAllowedInHistory = false,
                IsRoamable = false,
            });

            StopTimerNoLock();
            _latestWrite = write;

            var seconds = autoClearSeconds
                ?? ClipboardClearPolicy.SecondsForIndex(AppPreferences.Current.ClearClipboardIndex);
            if (seconds <= 0)
                return;

            var dispatcher = DispatcherQueue.GetForCurrentThread();
            if (dispatcher is null)
                return;

            var timer = dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(seconds);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => OnClearTimer(write, timer);
            _clearTimer = timer;
            timer.Start();
        }
    }

    public Task ClearSecretsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ClearLatestOwned("ClipboardClear");
        return Task.CompletedTask;
    }

    public void ClearSecretsNow() => ClearLatestOwned("ClipboardClear");

    private void OnClearTimer(ClipboardWrite write, DispatcherQueueTimer timer)
    {
        lock (_gate)
        {
            timer.Stop();
            if (!ReferenceEquals(_clearTimer, timer))
                return;

            _clearTimer = null;
            if (!ReferenceEquals(_latestWrite, write))
                return;

            if (TryClearIfOwnedNoLock("ClipboardTimedClear"))
                _latestWrite = null;
        }
    }

    private void ClearLatestOwned(string operation)
    {
        lock (_gate)
        {
            StopTimerNoLock();
            if (TryClearIfOwnedNoLock(operation))
                _latestWrite = null;
        }
    }

    private void StopTimerNoLock()
    {
        _clearTimer?.Stop();
        _clearTimer = null;
    }

    private static bool TryClearIfOwnedNoLock(string operation)
    {
        try
        {
            var current = Clipboard.GetContent();
            if (current.Contains(OwnershipFormat))
                Clipboard.Clear();
            return true;
        }
        catch (Exception ex)
        {
            CrashLogger.WriteRedacted(operation, ex);
            return false;
        }
    }
}
