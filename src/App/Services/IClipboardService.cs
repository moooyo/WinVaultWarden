namespace App.Services;

// 剪贴板抽象。让 VaultViewModel 不直接依赖 WinUI 的 Clipboard API,
// 从而可被纯 net 测试项目链接编译。WinUI 实现见 ClipboardService。
public interface IClipboardService
{
    void SetText(string text);
}
