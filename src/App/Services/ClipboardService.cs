using Windows.ApplicationModel.DataTransfer;

namespace App.Services;

// WinUI 剪贴板实现。注入到 VaultViewModel。
public sealed class ClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }
}
