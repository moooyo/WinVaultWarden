using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace App.Services;

// WinUI 剪贴板实现。SetText 用于非敏感字段;SetSecretText 用于密码/TOTP/Send 链接:
// 禁止进入剪贴板历史(Win+V)与云漫游,并在超时后(若内容未被替换)自动清除。
public sealed class ClipboardService : IClipboardService
{
    private DispatcherQueueTimer? _clearTimer;
    // 记录本应用最近一次写入剪贴板的密钥,供锁定/登出时 compare-then-clear。
    private string? _lastSecret;

    public void SetText(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    public void SetSecretText(string text, int? autoClearSeconds = null)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContentWithOptions(dp, new ClipboardContentOptions
        {
            IsAllowedInHistory = false,
            IsRoamable = false,
        });

        // 记录本次写入值:即便下方因 seconds<=0 提前返回,锁定/登出时仍能据此主动清除。
        _lastSecret = text;

        // 取消上一个尚未触发的清除定时器。
        _clearTimer?.Stop();

        var seconds = autoClearSeconds ?? ClipboardClearPolicy.SecondsForIndex(AppPreferences.Current.ClearClipboardIndex);
        if (seconds <= 0)
            return;

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
            return;

        _clearTimer = dispatcher.CreateTimer();
        _clearTimer.Interval = TimeSpan.FromSeconds(seconds);
        _clearTimer.IsRepeating = false;
        _clearTimer.Tick += (timer, _) =>
        {
            timer.Stop();
            _ = ClearIfUnchangedAsync(text);
        };
        _clearTimer.Start();
    }

    // 锁定/登出时主动清除:停掉自动清定时器,并把本应用写入的密钥立即从剪贴板清掉。
    // 同步 fail-closed 路径:直接 Clipboard.Clear()(不做 compare-then-clear 的异步读取),
    // 任何失败脱敏上报后吞掉,绝不外抛——安全清理不得因剪贴板不可用而中断。
    public async Task ClearSecretsAsync(CancellationToken ct = default)
    {
        _clearTimer?.Stop();
        var expected = Interlocked.Exchange(ref _lastSecret, null);
        if (string.IsNullOrEmpty(expected))
            return;
        ct.ThrowIfCancellationRequested();
        await ClearIfUnchangedAsync(expected);
    }

    public void ClearSecretsNow()
    {
        _clearTimer?.Stop();
        var hadSecret = !string.IsNullOrEmpty(Interlocked.Exchange(ref _lastSecret, null));
        if (!hadSecret)
            return;
        try { Clipboard.Clear(); }
        catch (Exception ex)
        {
            CrashLogger.WriteRedacted("ClipboardClear", ex);
        }
    }

    // 仅当剪贴板当前文本仍是本次写入的密钥时才清除,避免清掉用户随后复制的其他内容。
    // 闭包捕获本次写入值(而非共享字段),消除快速连续复制时的竞态。
    private static async Task ClearIfUnchangedAsync(string expected)
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view.Contains(StandardDataFormats.Text))
            {
                var current = await view.GetTextAsync();
                if (current == expected)
                    Clipboard.Clear();
            }
        }
        catch
        {
            // 剪贴板被其他进程占用等:忽略,下次复制会重置定时。
        }
    }
}
