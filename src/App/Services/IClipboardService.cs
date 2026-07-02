namespace App.Services;

// 剪贴板抽象。让 ViewModel 不直接依赖 WinUI 的 Clipboard API,
// 从而可被纯 net 测试项目链接编译。WinUI 实现见 ClipboardService。
public interface IClipboardService
{
    void SetText(string text);

    // 敏感值(密码/TOTP/Send 链接)复制:加固实现负责自动清除 + 剪贴板历史隔离。
    // 默认回退到 SetText,保证未覆写的实现(测试替身)仍可编译且能复制到值。
    // null = 用「清空剪贴板」设置;显式秒数 = 覆盖。默认接口方法回退到 SetText(不清)。
    void SetSecretText(string text, int? autoClearSeconds = null) => SetText(text);
}
