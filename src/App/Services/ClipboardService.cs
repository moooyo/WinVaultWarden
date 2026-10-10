using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace App.Services;

/// <summary>
/// Applies the protected clipboard policy to every vault-derived value.
/// An application-owned clipboard format protects external content, while an in-memory write
/// identity prevents an expired timer from clearing a newer application write.
/// </summary>
public sealed class ClipboardService : IClipboardService, IClipboardStatus
{
    private const string OwnershipFormat = "WinVaultWarden.VaultClipboard";

    private readonly object _gate = new();
    private DispatcherQueueTimer? _clearTimer;
    private ClipboardWrite? _latestWrite;

    public long WriteVersion { get; private set; }
    public DateTimeOffset? ClearAt { get; private set; }
    public int TotalSeconds { get; private set; }
    public bool HasContent => _latestWrite is not null;
    public DateTimeOffset? LastClearedAt { get; private set; }
    public string Label { get; private set; } = "内容";
    public event EventHandler? Changed;

    public void SetLabel(string label)
    {
        Label = string.IsNullOrWhiteSpace(label) ? "内容" : label;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ClipboardService()
    {
        Clipboard.ContentChanged += OnClipboardContentChanged;
    }

    private void OnClipboardContentChanged(object? sender, object args)
    {
        lock (_gate)
        {
            if (_latestWrite is null)
                return;
            try
            {
                if (Clipboard.GetContent().Contains(OwnershipFormat))
                    return;
                StopTimerNoLock();
                _latestWrite = null;
                ClearAt = null;
                LastClearedAt = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                CrashLogger.WriteRedacted("ClipboardOwnershipChanged", ex);
            }
        }
    }

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
            if (!Clipboard.SetContentWithOptions(package, new ClipboardContentOptions
            {
                IsAllowedInHistory = !AppPreferences.Current.ExcludeClipboardHistory,
                IsRoamable = false,
            }))
                throw new InvalidOperationException("The protected clipboard write was not accepted.");

            StopTimerNoLock();
            _latestWrite = write;
            WriteVersion++;
            LastClearedAt = null;
            Label = "内容";

            var seconds = autoClearSeconds
                ?? ClipboardClearPolicy.SecondsForIndex(AppPreferences.Current.ClearClipboardIndex);
            TotalSeconds = seconds;
            ClearAt = seconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null;
            Changed?.Invoke(this, EventArgs.Empty);
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

            CompleteClearNoLock("ClipboardTimedClear");
        }
    }

    private void ClearLatestOwned(string operation)
    {
        lock (_gate)
        {
            StopTimerNoLock();
            CompleteClearNoLock(operation);
        }
    }

    private void CompleteClearNoLock(string operation)
    {
        var hadContent = _latestWrite is not null;
        if (!TryClearIfOwnedNoLock(operation, out var cleared))
            return;
        _latestWrite = null;
        ClearAt = null;
        LastClearedAt = hadContent && cleared ? DateTimeOffset.UtcNow : null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StopTimerNoLock()
    {
        _clearTimer?.Stop();
        _clearTimer = null;
    }

    private static bool TryClearIfOwnedNoLock(string operation, out bool cleared)
    {
        cleared = false;
        try
        {
            var current = Clipboard.GetContent();
            if (current.Contains(OwnershipFormat))
            {
                Clipboard.Clear();
                cleared = true;
            }
            return true;
        }
        catch (Exception ex)
        {
            CrashLogger.WriteRedacted(operation, ex);
            return false;
        }
    }
}
