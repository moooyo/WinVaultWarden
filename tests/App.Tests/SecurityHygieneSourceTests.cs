using Xunit;

namespace App.Tests;

/// <summary>
/// S2/S3 源结构守卫。关闭到托盘先锁定、FieldRow 揭示态跨条目复位均属 WinUI 运行时行为、
/// 无法单测,故扫描源文件锁定关键结构不被回退(与 GeneratorDialogFlowTests 一致)。
/// </summary>
public class SecurityHygieneSourceTests
{
    private static string ReadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join('/', relativeParts));
    }

    // ── S3: FieldRow 值变更时复位揭示态 ──────────────────────────────────────

    [Fact]
    public void FieldRow_ValueChange_RemasksRevealedSecret()
    {
        var src = ReadSource("src", "App", "Controls", "FieldRow.xaml.cs");

        // Value 变更回调不再只 Render,而是经 OnValueChanged 复位已揭示的机密。
        Assert.Contains("OnValueChanged()", src);
        Assert.Contains("if (IsSecret && _revealed)", src);
        Assert.Contains("SetRevealed(false)", src);
    }

    // ── S2: 关闭到托盘前先锁定 ───────────────────────────────────────────────

    [Fact]
    public void CloseToTray_LocksBeforeHiding()
    {
        var src = ReadSource("src", "App", "MainWindow.xaml.cs");

        var idx = src.IndexOf("ShouldHideOnClose", StringComparison.Ordinal);
        Assert.True(idx >= 0, "close-to-tray 分支未找到");
        var block = src.Substring(idx, Math.Min(500, src.Length - idx));

        var lockIdx = block.IndexOf("LockNow", StringComparison.Ordinal);
        var hideIdx = block.IndexOf("AppWindow.Hide", StringComparison.Ordinal);
        Assert.True(lockIdx >= 0, "close-to-tray 未调用 LockNow");
        Assert.True(hideIdx >= 0, "close-to-tray 未调用 Hide");
        Assert.True(lockIdx < hideIdx, "LockNow 必须在 Hide 之前");
    }

    // ── S4: 关闭"允许截图"→ 对窗口设 WDA_EXCLUDEFROMCAPTURE ──────────────────

    [Fact]
    public void Screenshots_AffinityWiredToSetting()
    {
        var native = ReadSource("src", "App", "Interop", "NativeMethods.cs");
        Assert.Contains("SetWindowDisplayAffinity", native);
        Assert.Contains("WDA_EXCLUDEFROMCAPTURE", native);

        var window = ReadSource("src", "App", "MainWindow.xaml.cs");
        Assert.Contains("ApplyScreenshotSetting", window);
        Assert.Contains("WDA_EXCLUDEFROMCAPTURE", window);
    }

    // ── S5: 浏览器集成开关门控桥接启停 ───────────────────────────────────────

    [Fact]
    public void BrowserIntegration_GatesBridgeStartStop()
    {
        var app = ReadSource("src", "App", "App.xaml.cs");
        // 仅当 AllowBrowserIntegration 时才 Start 桥接。
        Assert.Contains("AllowBrowserIntegration", app);
        Assert.Contains("PasskeyBridgeServer", app);
        Assert.Contains("AllowBrowserIntegrationSettingChanged", app);

        var bridge = ReadSource("src", "App", "Services", "PasskeyBridgeServer.cs");
        Assert.Contains("public async Task StopAsync()", bridge); // 可停止/可重启
    }

    // ── S9: TOTP 字段显示计算出的验证码,不直显/复制原始种子 ────────────────

    [Fact]
    public void TotpField_ShowsComputedCode_NotRawSecret()
    {
        var totp = ReadSource("src", "App", "Controls", "TotpField.xaml.cs");
        Assert.Contains("TotpGenerator.TryGenerate", totp);
        Assert.Contains("_currentCode", totp);        // 复制的是计算出的码
        Assert.DoesNotContain("SetSecretText(value)", totp); // 不再复制原始 Code/种子

        var page = ReadSource("src", "App", "Views", "VaultPage.xaml");
        Assert.Contains("TotpField Secret=", page);   // 绑定改为 Secret(输入种子),非 Code
    }
}
