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

    // S3: Changing the value or visual context invalidates revealed secrets.

    [Fact]
    public void FieldRow_ValueChange_RemasksRevealedSecret()
    {
        var src = ReadSource("src", "App", "Controls", "FieldRow.xaml.cs");

        Assert.Contains("nameof(Value), typeof(string), typeof(FieldRow)", src);
        Assert.Contains("(d, _) => ((FieldRow)d).ResetField()", src);
        Assert.Contains("DataContextChanged += (_, _) => ResetField()", src);
        var resetStart = src.IndexOf("private void ResetField()", StringComparison.Ordinal);
        var resetEnd = src.IndexOf("private void Render()", resetStart, StringComparison.Ordinal);
        Assert.True(resetStart >= 0 && resetEnd > resetStart);
        var reset = src[resetStart..resetEnd];
        Assert.Contains("_viewVersion++", reset);
        Assert.Contains("_operationCancellation?.Cancel()", reset);
        Assert.Contains("_revealed = false", reset);
        Assert.Contains("ClearFeedback()", reset);
    }

    // ── S2: 关闭到托盘前先锁定(经生命周期协调器 Lock intent) ───────────────

    [Fact]
    public void CloseToTray_LocksBeforeHiding()
    {
        var src = ReadSource("src", "App", "MainWindow.xaml.cs");

        var idx = src.IndexOf("ShouldHideOnClose", StringComparison.Ordinal);
        Assert.True(idx >= 0, "close-to-tray 分支未找到");
        var block = src.Substring(idx, Math.Min(500, src.Length - idx));

        var lockIdx = block.IndexOf("AppLifecycleIntent.Lock", StringComparison.Ordinal);
        var hideIdx = block.IndexOf(".Hide()", StringComparison.Ordinal);
        Assert.True(lockIdx >= 0, "close-to-tray 未请求 Lock intent");
        Assert.True(hideIdx >= 0, "close-to-tray 未调用 Hide");
        Assert.True(lockIdx < hideIdx, "Lock intent 必须在 Hide 之前");
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

    // S9: Render and copy generated codes, never the TOTP seed.

    [Fact]
    public void TotpField_ShowsComputedCode_NotRawSecret()
    {
        var totp = ReadSource("src", "App", "Controls", "TotpField.xaml.cs");
        Assert.Contains("TotpGenerator.TryGenerate", totp);
        Assert.Contains("_currentCode", totp);
        Assert.Contains("clipboard.SetSecretText(code)", totp);
        Assert.DoesNotContain("SetSecretText(Secret)", totp);
        Assert.DoesNotContain("CodeText.Text = Secret", totp);
        Assert.Contains("_currentCode = null", totp);

        var page = ReadSource("src", "App", "Views", "VaultPage.xaml");
        Assert.Contains("controls:CopyStrip", page);
        var copyStrip = ReadSource("src", "App", "Controls", "CopyStrip.xaml.cs");
        Assert.Contains("new TotpField { IsCompact = true, IsReadOnly = true }", copyStrip);
        Assert.Contains("cell.Totp.Secret = value ?? string.Empty", copyStrip);
        Assert.Contains("clipboard.SetSecretText(code)", copyStrip);
        Assert.Contains("Crypto.TotpGenerator.TryGenerate(value", copyStrip);
    }

    // The page owns item reprompt; each field reveal also verifies the current user.

    [Fact]
    public void FieldRow_VerifiesRevealAndKeepsItemRepromptAtThePageBoundary()
    {
        var src = ReadSource("src", "App", "Controls", "FieldRow.xaml.cs");
        Assert.DoesNotContain("PassesRepromptAsync", src);
        Assert.DoesNotContain("IMasterPasswordReprompt", src);
        Assert.DoesNotContain("RepromptRequired", src);
        Assert.Contains("ISensitiveActionVerificationService", src);
        var revealStart = src.IndexOf("private async void OnReveal", StringComparison.Ordinal);
        var revealEnd = src.IndexOf("internal static async Task<bool> VerifyRevealAsync", revealStart, StringComparison.Ordinal);
        Assert.True(revealStart >= 0 && revealEnd > revealStart);
        var reveal = src[revealStart..revealEnd];
        Assert.Equal(2, reveal.Split("AuthorizeCurrentItemAsync(version, value, operationToken)", StringSplitOptions.None).Length - 1);
        var verification = reveal.IndexOf("await VerifyRevealAsync(this, Label, itemName, operationToken)", StringComparison.Ordinal);
        var authorizationAfterVerification = reveal.IndexOf("AuthorizeCurrentItemAsync", verification, StringComparison.Ordinal);
        var sensitiveWrite = reveal.IndexOf("_revealed = true", StringComparison.Ordinal);
        Assert.True(verification >= 0 && authorizationAfterVerification > verification && sensitiveWrite > authorizationAfterVerification);
        Assert.Contains("lifecycle?.TryRunSensitive", reveal);
        Assert.Contains("if (!IsCurrentOperation(version, value, operationToken)) return false", reveal);
        Assert.Contains("!ct.IsCancellationRequested && IsLoaded && version == _viewVersion", src);
        Assert.Contains("string.Equals(Value, value, StringComparison.Ordinal)", src);
        Assert.Contains("var authorize = AuthorizeItemAsync", src);

        var page = ReadSource("src", "App", "Views", "VaultPage.xaml");
        Assert.DoesNotContain("RepromptRequired", page);
        var pageSource = ReadSource("src", "App", "Views", "VaultPage.xaml.cs");
        Assert.Contains("field.AuthorizeItemAsync =", pageSource);
        Assert.Contains("AuthorizeFieldCopyAsync", pageSource);
    }
}
