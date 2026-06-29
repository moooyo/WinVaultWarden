using System.Xml.Linq;
using Xunit;

namespace App.Tests;

/// <summary>
/// Task 7 XAML 结构测试：断言 SettingsPage 三个账户操作的触发器元素存在，
/// 并通过扫描 code-behind 源码验证各 ContentDialog 的关键组件已声明。
/// 对话框控件由 code-behind 动态创建，不在 XAML 中静态声明；
/// 此处验证：
///   1. XAML 中对应触发器（Button/SettingsCard Click handler）已连接
///   2. code-behind 中包含各 AutomationId 字符串（对话框输入控件存在的最可靠证据）
/// </summary>
public class SettingsPageXamlTests
{
    private static XDocument LoadXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "SettingsPage.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("SettingsPage.xaml not found.");
    }

    private static string LoadCodeBehind()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "SettingsPage.xaml.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("SettingsPage.xaml.cs not found.");
    }

    // ── XAML 触发器断言 ───────────────────────────────────────────────────────

    [Fact]
    public void EditProfileButton_HasClickHandler()
    {
        var doc = LoadXaml();
        var button = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Button"
                && (string?)e.Attribute("Click") == "OnEditProfileClick");
        Assert.NotNull(button);
    }

    [Fact]
    public void EditProfileButton_HasAutomationId()
    {
        var doc = LoadXaml();
        // AutomationProperties.AutomationId 在 XAML 中是附加属性，XML 属性名为 "AutomationProperties.AutomationId"
        var button = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Button"
                && e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.AutomationId"
                    && a.Value == "EditProfileButton"));
        Assert.NotNull(button);
    }

    [Fact]
    public void ChangePasswordCard_HasClickHandler()
    {
        var doc = LoadXaml();
        var card = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "SettingsCard"
                && (string?)e.Attribute("Click") == "OnChangePasswordCardClick");
        Assert.NotNull(card);
    }

    [Fact]
    public void ChangePasswordCard_HasAutomationId()
    {
        var doc = LoadXaml();
        var card = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "SettingsCard"
                && e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.AutomationId"
                    && a.Value == "ChangePasswordCard"));
        Assert.NotNull(card);
    }

    [Fact]
    public void ChangeKdfCard_HasClickHandler()
    {
        var doc = LoadXaml();
        var card = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "SettingsCard"
                && (string?)e.Attribute("Click") == "OnChangeKdfCardClick");
        Assert.NotNull(card);
    }

    [Fact]
    public void ChangeKdfCard_HasAutomationId()
    {
        var doc = LoadXaml();
        var card = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "SettingsCard"
                && e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.AutomationId"
                    && a.Value == "ChangeKdfCard"));
        Assert.NotNull(card);
    }

    // ── code-behind 对话框控件断言 ────────────────────────────────────────────

    [Fact]
    public void EditProfileDialog_NameBoxDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("EditProfileNameBox", src, StringComparison.Ordinal);
    }

    [Fact]
    public void EditProfileDialog_ErrorTextDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("EditProfileErrorText", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangePasswordDialog_CurrentBoxDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("ChangePasswordCurrentBox", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangePasswordDialog_NewBoxDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("ChangePasswordNewBox", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangePasswordDialog_ConfirmBoxDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("ChangePasswordConfirmBox", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangePasswordDialog_HintBoxDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("ChangePasswordHintBox", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangePasswordDialog_ErrorTextDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("ChangePasswordErrorText", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeKdfDialog_CurrentBoxDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("ChangeKdfCurrentBox", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeKdfDialog_IterationsBoxDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("ChangeKdfIterationsBox", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeKdfDialog_ErrorTextDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("ChangeKdfErrorText", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeKdfDialog_MinimumIs100000()
    {
        var src = LoadCodeBehind();
        // NumberBox.Minimum = 100000 必须出现在 ChangeKdf 的上下文中
        Assert.Contains("Minimum = 100000", src, StringComparison.Ordinal);
    }

    // ── 成功后导航行为断言 ────────────────────────────────────────────────────

    [Fact]
    public void OnSuccess_CallsNavigateToLogin()
    {
        var src = LoadCodeBehind();
        Assert.Contains("NavigateToLogin", src, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigateToLogin_CallsLockAsync()
    {
        var src = LoadCodeBehind();
        Assert.Contains("LockAsync", src, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigateToLogin_CallsShowLogin()
    {
        var src = LoadCodeBehind();
        Assert.Contains("ShowLogin", src, StringComparison.Ordinal);
    }

    // ── Task 7: TwoFactorCard 断言 ────────────────────────────────────────────

    [Fact]
    public void TwoFactorCard_HasAutomationId()
    {
        var doc = LoadXaml();
        var card = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "SettingsCard"
                && e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.AutomationId"
                    && a.Value == "TwoFactorCard"));
        Assert.NotNull(card);
    }

    [Fact]
    public void TwoFactorCard_HasClickHandler()
    {
        var doc = LoadXaml();
        var card = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "SettingsCard"
                && (string?)e.Attribute("Click") == "OnTwoFactorCardClick");
        Assert.NotNull(card);
    }

    [Fact]
    public void TwoFactorCard_IsClickEnabled()
    {
        var doc = LoadXaml();
        var card = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "SettingsCard"
                && e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.AutomationId"
                    && a.Value == "TwoFactorCard"));
        Assert.NotNull(card);
        Assert.Equal("True", (string?)card.Attribute("IsClickEnabled"));
    }

    [Fact]
    public void TwoFactorCard_InSecuritySection()
    {
        var doc = LoadXaml();
        // 安全区 StackPanel 内有一个 TextBlock Text="安全"，且该 StackPanel 中包含 TwoFactorCard
        var securityPanel = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "StackPanel"
                && e.Descendants()
                    .Any(tb => tb.Name.LocalName == "TextBlock"
                        && (string?)tb.Attribute("Text") == "安全")
                && e.Descendants()
                    .Any(sc => sc.Name.LocalName == "SettingsCard"
                        && sc.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.AutomationId"
                            && a.Value == "TwoFactorCard")));
        Assert.NotNull(securityPanel);
    }

    [Fact]
    public void TotpSetup_PasswordBoxDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("TotpSetupPasswordBox", src, StringComparison.Ordinal);
    }

    [Fact]
    public void TotpSetup_SecretTextDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("TotpSetupSecretText", src, StringComparison.Ordinal);
    }

    [Fact]
    public void TotpSetup_OtpauthTextDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("TotpSetupOtpauthText", src, StringComparison.Ordinal);
    }

    [Fact]
    public void TotpSetup_CodeBoxDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("TotpSetupCodeBox", src, StringComparison.Ordinal);
    }

    [Fact]
    public void TotpSetup_RecoveryCodeTextDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("TotpSetupRecoveryCodeText", src, StringComparison.Ordinal);
    }

    [Fact]
    public void EmailSetup_PasswordBoxDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("EmailSetupPasswordBox", src, StringComparison.Ordinal);
    }

    [Fact]
    public void EmailSetup_AddressBoxDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("EmailSetupAddressBox", src, StringComparison.Ordinal);
    }

    [Fact]
    public void EmailSetup_TokenBoxDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("EmailSetupTokenBox", src, StringComparison.Ordinal);
    }

    [Fact]
    public void DisableTwoFactor_PasswordBoxDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("DisableTwoFactorPasswordBox", src, StringComparison.Ordinal);
    }

    [Fact]
    public void DisableTwoFactor_ProviderComboDeclared()
    {
        var src = LoadCodeBehind();
        Assert.Contains("DisableTwoFactorProviderCombo", src, StringComparison.Ordinal);
    }

    [Fact]
    public void OnTwoFactorCardClick_CallsListProvidersAsync()
    {
        var src = LoadCodeBehind();
        Assert.Contains("ListProvidersAsync", src, StringComparison.Ordinal);
    }

    [Fact]
    public void EnableTotp_CallsBeginTotpSetupAsync()
    {
        var src = LoadCodeBehind();
        Assert.Contains("BeginTotpSetupAsync", src, StringComparison.Ordinal);
    }

    [Fact]
    public void EnableTotp_CallsEnableTotpAsync()
    {
        var src = LoadCodeBehind();
        Assert.Contains("EnableTotpAsync", src, StringComparison.Ordinal);
    }

    [Fact]
    public void EnableEmail_CallsSendEmailAsync()
    {
        var src = LoadCodeBehind();
        Assert.Contains("SendEmailAsync", src, StringComparison.Ordinal);
    }

    [Fact]
    public void EnableEmail_CallsEnableEmailAsync()
    {
        var src = LoadCodeBehind();
        Assert.Contains("EnableEmailAsync", src, StringComparison.Ordinal);
    }

    [Fact]
    public void DisableTwoFactor_CallsDisableTwoFactorAsync()
    {
        var src = LoadCodeBehind();
        Assert.Contains("DisableTwoFactorAsync", src, StringComparison.Ordinal);
    }
}
