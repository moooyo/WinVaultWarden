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
    public void ChangeKdfDialog_UsesWriteCompatiblePbkdf2Range()
    {
        var src = LoadCodeBehind();
        Assert.Contains(
            "Minimum = KdfParameterPolicy.MinimumPbkdf2WriteIterations",
            src,
            StringComparison.Ordinal);
        Assert.Contains(
            "Maximum = KdfParameterPolicy.MaximumPbkdf2WriteIterations",
            src,
            StringComparison.Ordinal);
    }

    // ── 成功后交由生命周期协调器统一处理(页面内不再直接锁定/导航) ────────────────

    [Fact]
    public void OnSuccess_DoesNotNavigateInPage()
    {
        var src = LoadCodeBehind();
        // Task 2:改密/改 KDF 成功后页面不再 NavigateToLogin;SettingsViewModel 发 CredentialChanged intent,
        // 由唯一生命周期协调器统一登出并回登录页。
        Assert.DoesNotContain("NavigateToLogin", src, StringComparison.Ordinal);
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
    public void TwoFactorCard_AdvertisesSecurityKeyManagement()
    {
        var card = LoadXaml().Descendants()
            .First(element => element.Name.LocalName == "SettingsCard"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "AutomationProperties.AutomationId"
                    && attribute.Value == "TwoFactorCard"));

        Assert.Contains("安全密钥", (string?)card.Attribute("Description"), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFactorDialog_ExposesWebAuthnManagementAction()
    {
        var source = LoadCodeBehind();

        Assert.Contains("TwoFactorManageWebAuthnButton", source, StringComparison.Ordinal);
        Assert.Contains("ShowManageWebAuthnDialogAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WebAuthnManagement_DeclaresAccessibleCredentialControls()
    {
        var source = LoadCodeBehind();

        Assert.Contains("WebAuthnVerificationMethodCombo", source, StringComparison.Ordinal);
        Assert.Contains("WebAuthnVerificationInputBox", source, StringComparison.Ordinal);
        Assert.Contains("WebAuthnRequestOtpButton", source, StringComparison.Ordinal);
        Assert.Contains("RequestWebAuthnVerificationOtpAsync", source, StringComparison.Ordinal);
        Assert.Contains("WebAuthnCredentialList", source, StringComparison.Ordinal);
        Assert.Contains("WebAuthnCredentialNameBox", source, StringComparison.Ordinal);
        Assert.Contains("WebAuthnDeleteCredentialButton_", source, StringComparison.Ordinal);
        Assert.Contains("WebAuthnManagementStatusBar", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WebAuthnManagement_HandlesLastCredentialAsProviderDisable()
    {
        var source = LoadCodeBehind();

        Assert.Contains("snapshot.Credentials.Count == 1", source, StringComparison.Ordinal);
        Assert.Contains("将关闭 WebAuthn 双重验证", source, StringComparison.Ordinal);
        Assert.Contains("DeleteWebAuthnCredentialAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFactorProviderLabels_UseCanonicalEnumValues()
    {
        var source = LoadCodeBehind();

        Assert.Contains("GetTwoFactorProviderDisplayName", source, StringComparison.Ordinal);
        Assert.DoesNotContain("4 => \"FIDO2/WebAuthn", source, StringComparison.Ordinal);
        Assert.DoesNotContain("5 => \"Duo", source, StringComparison.Ordinal);
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
    public void OnTwoFactorCardClick_CallsLoadProvidersAsync()
    {
        var src = LoadCodeBehind();
        Assert.Contains("LoadProvidersAsync", src, StringComparison.Ordinal);
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

    // ── Task 6: 显示网站图标 开关 ─────────────────────────────────────────────

    [Fact]
    public void Settings_HasShowWebsiteIconsToggle()
    {
        var doc = LoadXaml();
        Assert.Contains("ShowWebsiteIcons", doc.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsControls_HaveAccessibleNames()
    {
        var controls = LoadXaml().Descendants()
            .Where(element => element.Name.LocalName is "ComboBox" or "ToggleSwitch")
            .ToList();

        Assert.NotEmpty(controls);
        Assert.All(controls, control => Assert.False(string.IsNullOrWhiteSpace(
            control.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name")?.Value)));
    }

    [Fact]
    public void SettingsComboBoxes_StretchWithoutFixedMinimumWidth()
    {
        var comboBoxes = LoadXaml().Descendants()
            .Where(element => element.Name.LocalName == "ComboBox")
            .ToList();

        Assert.NotEmpty(comboBoxes);
        Assert.All(comboBoxes, comboBox =>
        {
            Assert.Equal("0", (string?)comboBox.Attribute("MinWidth"));
            Assert.Equal("Stretch", (string?)comboBox.Attribute("HorizontalAlignment"));
        });
    }

    [Fact]
    public void SettingsPage_ExposesHeadingHierarchy()
    {
        var headings = LoadXaml().Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Select(element => element.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName == "AutomationProperties.HeadingLevel")?.Value)
            .Where(value => value is not null)
            .ToList();

        Assert.Equal(7, headings.Count);
        Assert.Single(headings, value => value == "Level1");
        Assert.Equal(6, headings.Count(value => value == "Level2"));
    }

    [Fact]
    public void TrayAndHelloToggles_BindDerivedAvailability()
    {
        var source = LoadXaml().ToString();

        Assert.Contains("CanConfigureTrayBehavior", source, StringComparison.Ordinal);
        Assert.Contains("CanToggleWindowsHello", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DisableTwoFactorDialog_DefaultsToCancel()
    {
        var source = LoadCodeBehind();
        var start = source.IndexOf("var disableDialog = new ContentDialog", StringComparison.Ordinal);

        Assert.True(start >= 0);
        var section = source[start..Math.Min(source.Length, start + 500)];
        Assert.Contains("DefaultButton = ContentDialogButton.Close", section, StringComparison.Ordinal);
    }
}
