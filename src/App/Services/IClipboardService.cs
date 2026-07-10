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

    // 锁定/登出时主动清除本应用写入剪贴板的密钥(compare-then-clear,只清仍是本应用写入的值)。
    // 默认空实现,保证未覆写的实现(测试替身)仍可编译。
    void ClearSecretsNow() { }

    // 可等待的清理:供生命周期协调器在异步清理阶段 await,确保剪贴板密钥先清干净再继续。
    // 默认回退到同步 ClearSecretsNow,保证未覆写的实现(测试替身)仍可编译。
    Task ClearSecretsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ClearSecretsNow();
        return Task.CompletedTask;
    }
}
