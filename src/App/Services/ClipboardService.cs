using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace App.Services;

// WinUI 剪贴板实现。SetText 用于非敏感字段;SetSecretText 用于密码/TOTP/Send 链接:
// 禁止进入剪贴板历史(Win+V)与云漫游,并在超时后(若内容未被替换)自动清除。
public sealed class ClipboardService : IClipboardService
{
    private DispatcherQueueTimer? _clearTimer;
    private string? _pendingSecret;

    public void SetText(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    public void SetSecretText(string text, int autoClearSeconds = 30)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContentWithOptions(dp, new ClipboardContentOptions
        {
            IsAllowedInHistory = false,
            IsRoamable = false,
        });

        _pendingSecret = text;
        _clearTimer?.Stop();
        if (autoClearSeconds <= 0)
            return;

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
            return;

        _clearTimer = dispatcher.CreateTimer();
        _clearTimer.Interval = TimeSpan.FromSeconds(autoClearSeconds);
        _clearTimer.IsRepeating = false;
        _clearTimer.Tick += (timer, _) =>
        {
            timer.Stop();
            _ = ClearIfUnchangedAsync();
        };
        _clearTimer.Start();
    }

    // 仅当剪贴板当前文本仍是本次写入的密钥时才清除,避免清掉用户随后复制的其他内容。
    private async Task ClearIfUnchangedAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view.Contains(StandardDataFormats.Text))
            {
                var current = await view.GetTextAsync();
                if (current == _pendingSecret)
                    Clipboard.Clear();
            }
        }
        catch
        {
            // 剪贴板被其他进程占用等:忽略,下次复制会重置定时。
        }
        finally
        {
            _pendingSecret = null;
        }
    }
}
