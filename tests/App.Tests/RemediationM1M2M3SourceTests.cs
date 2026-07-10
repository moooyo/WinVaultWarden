using Xunit;

namespace App.Tests;

/// <summary>
/// Task 3 代码审查 M1/M2/M3 修复守卫：
/// M1 — TOTP/Email 向导 clearSensitive 须清验证码 TextBox 及密钥/恢复码 TextBlock。
/// M2 — Generator 历史被强制关闭后，循环须检查真实 VaultState 仍 Unlocked 再继续。
/// M3 — DialogHelper 关闭回调须经 DispatcherQueue.TryEnqueue，不得直接跨线程调 Hide。
/// </summary>
public sealed class RemediationM1M2M3SourceTests
{
    // ── M1: TOTP 向导 clearSensitive ────────────────────────────────────────

    [Fact]
    public void TotpSetup_Step2ClearSensitive_ClearsCodeBox()
    {
        // step2 ShowTrackedAsync 的 clearSensitive 除清主密码外还须清验证码输入框
        var src = ReadSettings();
        Assert.Contains("codeBox.Text = string.Empty", src);
    }

    [Fact]
    public void TotpSetup_Step2ClearSensitive_ClearsOtpauthText()
    {
        // step2 ShowTrackedAsync 的 clearSensitive 还须清除 otpauthText.Text，
        // 其 URI 中含 secret，不能残留在已关闭对话框里。
        var src = ReadSettings();
        Assert.Contains("otpauthText.Text = string.Empty", src);
    }

    [Fact]
    public void TotpSetup_Step3_ClearSensitive_ClearsRecoveryCodeBlock()
    {
        // step3 ShowTrackedAsync 须提供 clearSensitive 清恢复码 TextBlock.Text
        var src = ReadSettings();
        Assert.Contains("rcText.Text = string.Empty", src);
    }

    // ── M1: Email 向导 clearSensitive ────────────────────────────────────────

    [Fact]
    public void EmailSetup_Step2ClearSensitive_ClearsTokenBox()
    {
        // Email step2 ShowTrackedAsync 的 clearSensitive 除清主密码外还须清 token 输入框
        var src = ReadSettings();
        Assert.Contains("tokenBox.Text = string.Empty", src);
    }

    // ── M2: Generator 循环真实 VaultState 守卫 ─────────────────────────────

    [Fact]
    public void GeneratorLoop_ChecksVaultStateUnlockedAfterHistoryCloses()
    {
        // 历史对话框关闭后，循环须检查真实 VaultState == Unlocked 再继续，
        // 不能只依赖 coordinator IsTransitioning 窗口（可能已完成过渡）。
        var src = Read("MainWindow.xaml.cs");
        Assert.Contains("VaultState.Unlocked", src);
    }

    // ── M3: DialogHelper 使用 DispatcherQueue 关闭 ──────────────────────────

    [Fact]
    public void DialogHelper_CloseCallback_EnqueuedOnDispatcherQueue()
    {
        // 关闭回调不得直接 dialog.Hide（可能从非 UI 线程调用）,
        // 须经 dialog.DispatcherQueue.TryEnqueue(dialog.Hide)。
        var src = Read("Views", "DialogHelper.cs");
        Assert.Contains("DispatcherQueue.TryEnqueue", src);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string ReadSettings() => Read("Views", "SettingsPage.xaml.cs");

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([AppDirectory(), .. parts]));

    private static string AppDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "App");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("src\\App");
    }
}
