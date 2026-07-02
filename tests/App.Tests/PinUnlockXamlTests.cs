using Xunit;

namespace App.Tests;

/// <summary>
/// Task 6：PIN 解锁 UI —— SettingsPage 的 PIN 开关 + 设 PIN 对话框，
/// LoginPage 的 PinUnlock 阶段输入面板。字符串级断言，mirror VaultPageFilterBarXamlTests。
/// </summary>
public class PinUnlockXamlTests
{
    private static string LoadLoginPageXaml() => LoadText("LoginPage.xaml");

    private static string LoadLoginPageCodeBehind() => LoadText("LoginPage.xaml.cs");

    private static string LoadSettingsPageXaml() => LoadText("SettingsPage.xaml");

    private static string LoadSettingsPageCodeBehind() => LoadText("SettingsPage.xaml.cs");

    private static string LoadText(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", fileName);
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"{fileName} not found.");
    }

    // ── LoginPage: PinUnlock 面板 ────────────────────────────────────────────

    [Fact]
    public void LoginPage_PinBox_BoundTwoWay()
    {
        var xaml = LoadLoginPageXaml();
        Assert.Contains("Password=\"{x:Bind ViewModel.Pin, Mode=TwoWay}\"", xaml);
    }

    [Fact]
    public void LoginPage_PinUnlockPanel_VisibilityBoundToIsPinUnlockStage()
    {
        var xaml = LoadLoginPageXaml();
        Assert.Contains("Visibility=\"{x:Bind ViewModel.IsPinUnlockStage, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}\"", xaml);
    }

    [Fact]
    public void LoginPage_HasUseMasterPasswordCommand()
    {
        var xaml = LoadLoginPageXaml();
        Assert.Contains("Command=\"{x:Bind ViewModel.UseMasterPasswordCommand", xaml);
    }

    [Fact]
    public void LoginPage_HasUseMasterPasswordHyperlinkButton()
    {
        var xaml = LoadLoginPageXaml();
        Assert.Contains("HyperlinkButton", xaml);
        Assert.Contains("用主密码解锁", xaml);
    }

    [Fact]
    public void LoginPage_PinBox_HasAutomationId()
    {
        var xaml = LoadLoginPageXaml();
        Assert.Contains("AutomationProperties.AutomationId=\"PinBox\"", xaml);
    }

    // ── SettingsPage: PIN 开关 ───────────────────────────────────────────────

    [Fact]
    public void SettingsPage_HasPinUnlockCard()
    {
        var xaml = LoadSettingsPageXaml();
        Assert.Contains("PIN 解锁", xaml);
    }

    [Fact]
    public void SettingsPage_PinToggle_HasNameAndToggledHandler()
    {
        var xaml = LoadSettingsPageXaml();
        Assert.Contains("x:Name=\"PinToggle\"", xaml);
        Assert.Contains("Toggled=\"OnPinToggleToggled\"", xaml);
    }

    [Fact]
    public void SettingsPage_CodeBehind_HasOnPinToggleToggledHandler()
    {
        var src = LoadSettingsPageCodeBehind();
        Assert.Contains("OnPinToggleToggled", src, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPage_CodeBehind_HasSuppressPinToggleGuard()
    {
        var src = LoadSettingsPageCodeBehind();
        Assert.Contains("_suppressPinToggle", src, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPage_CodeBehind_PromptsForPinOnEnable()
    {
        var src = LoadSettingsPageCodeBehind();
        Assert.Contains("PromptSetPinAsync", src, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPage_CodeBehind_CallsSetPinAndClearPin()
    {
        var src = LoadSettingsPageCodeBehind();
        Assert.Contains("ViewModel.SetPin(", src, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ClearPin()", src, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPage_CodeBehind_SyncsToggleFromIsPinSetOnNavigate()
    {
        var src = LoadSettingsPageCodeBehind();
        Assert.Contains("ViewModel.IsPinSet", src, StringComparison.Ordinal);
    }
}
