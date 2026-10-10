using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace App.Tests;

/// <summary>
/// Guards settings row actions, child-page controls, and security dialog contracts.
/// </summary>
public class SettingsPageXamlTests
{
    private static XDocument LoadXaml(string file = "SettingsPage.xaml") =>
        XDocument.Load(FindView(file));

    private static string LoadCodeBehind(string file = "SettingsPage.xaml.cs") =>
        File.ReadAllText(FindView(file));

    private static string FindView(string file)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", file);
            if (File.Exists(path))
                return path;
        }
        throw new FileNotFoundException(file);
    }

    private static XElement FindControl(XDocument document, string type, string automationId) =>
        document.Descendants().Single(element => element.Name.LocalName == type
            && (string?)element.Attribute("AutomationProperties.AutomationId") == automationId);

    [Theory]
    [InlineData("EditProfileButton", "OnEditProfileClick")]
    [InlineData("ChangePasswordCard", "OnChangePasswordCardClick")]
    [InlineData("ChangeKdfCard", "OnChangeKdfCardClick")]
    [InlineData("TwoFactorCard", "OnTwoFactorCardClick")]
    public void SettingsRowActions_HaveStableIdsAndClickHandlers(string automationId, string handler)
    {
        var button = FindControl(LoadXaml(), "Button", automationId);
        Assert.Equal(handler, (string?)button.Attribute("Click"));
        Assert.NotEqual("False", (string?)button.Attribute("IsEnabled"));
        Assert.Contains(button.Ancestors(), element => element.Name.LocalName == "SettingsCard");
    }

    [Theory]
    [InlineData("EditProfileNameBox")]
    [InlineData("EditProfileErrorText")]
    [InlineData("ChangeKdfCurrentBox")]
    [InlineData("ChangeKdfIterationsBox")]
    [InlineData("ChangeKdfErrorText")]
    [InlineData("TotpSetupPasswordBox")]
    [InlineData("TotpSetupCodeBox")]
    [InlineData("TotpSetupRecoveryCodeText")]
    [InlineData("EmailSetupPasswordBox")]
    [InlineData("EmailSetupAddressBox")]
    [InlineData("EmailSetupTokenBox")]
    [InlineData("DisableTwoFactorPasswordBox")]
    [InlineData("DisableTwoFactorProviderCombo")]
    public void SecurityDialogs_DeclareStableControlIds(string automationId)
    {
        Assert.Contains($"\"{automationId}\"", LoadCodeBehind(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PasswordBox", "ChangePasswordCurrentBox")]
    [InlineData("PasswordBox", "ChangePasswordNewBox")]
    [InlineData("PasswordBox", "ChangePasswordConfirmBox")]
    [InlineData("TextBox", "ChangePasswordHintBox")]
    [InlineData("InfoBar", "ChangePasswordErrorText")]
    public void ChangePasswordPage_PreservesAccessibleControlIds(string type, string automationId)
    {
        var control = FindControl(LoadXaml("SettingsPasswordPage.xaml"), type, automationId);
        if (type == "PasswordBox")
        {
            Assert.Equal("Hidden", (string?)control.Attribute("PasswordRevealMode"));
            Assert.False(string.IsNullOrWhiteSpace((string?)control.Attribute("AutomationProperties.Name")));
        }
    }

    [Fact]
    public void ChangePasswordAction_InitializesTheChildPageWithAccountReturnNavigation()
    {
        var source = LoadCodeBehind();
        Assert.Contains("new SettingsPasswordPage()", source, StringComparison.Ordinal);
        Assert.Contains("page.Initialize(ViewModel, () => SelectSection(\"account\"))", source, StringComparison.Ordinal);
        Assert.Contains("SettingsContentFrame.Content = page", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmedProfileRename_RefreshesTheSameSessionBeforePublishingShellContext()
    {
        var source = LoadCodeBehind();
        var start = source.IndexOf("private async Task EditProfileAsync()", StringComparison.Ordinal);
        var end = source.IndexOf("private void OnChangePasswordCardClick", start, StringComparison.Ordinal);
        var flow = source[start..end];
        Assert.Contains("DialogHelper.ShowTrackedAsync(dialog, expectedSession)", flow, StringComparison.Ordinal);
        Assert.Contains("token => sync.SyncAsync(expectedSession, token)", flow, StringComparison.Ordinal);
        Assert.Contains("lifecycle.TryRunSensitive(() => session.TryRunIfUnlocked(expectedSession", flow, StringComparison.Ordinal);
        Assert.Contains("MainWindow?.RefreshVaultSnapshot()", flow, StringComparison.Ordinal);
        Assert.Contains("ProfileRefreshPending", flow, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsContent_CentersInsideTheFullViewportWithoutCombiningPaddingAndMaxWidth()
    {
        var document = LoadXaml();
        var scroller = document.Descendants().Single(element => element.Name.LocalName == "ScrollViewer"
            && (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "SettingsScrollViewer");
        var layout = scroller.Elements().Single();
        var content = layout.Elements().Single();
        Assert.Equal("Grid", layout.Name.LocalName);
        Assert.Equal("40,40,40,48", (string?)layout.Attribute("Padding"));
        Assert.Null(layout.Attribute("MaxWidth"));
        Assert.Equal("680", (string?)content.Attribute("MaxWidth"));
        Assert.Equal("Stretch", (string?)content.Attribute("HorizontalAlignment"));
        Assert.Null(content.Attribute("Padding"));
    }

    [Fact]
    public void ChangeKdfDialog_UsesWriteCompatiblePbkdf2Range()
    {
        var source = LoadCodeBehind();
        Assert.Contains("Minimum = KdfParameterPolicy.MinimumPbkdf2WriteIterations", source, StringComparison.Ordinal);
        Assert.Contains("Maximum = KdfParameterPolicy.MaximumPbkdf2WriteIterations", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SettingsPage.xaml.cs")]
    [InlineData("SettingsPasswordPage.xaml.cs")]
    public void CredentialChanges_LeaveLoginNavigationToTheLifecycleCoordinator(string file)
    {
        Assert.DoesNotContain("NavigateToLogin", LoadCodeBehind(file), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFactorAction_InSecuritySection_AdvertisesSecurityKeyManagement()
    {
        var button = FindControl(LoadXaml(), "Button", "TwoFactorCard");
        var row = button.Ancestors().Single(element => element.Name.LocalName == "SettingsCard");
        Assert.Contains("安全密钥", (string?)row.Attribute("Description"), StringComparison.Ordinal);
        Assert.Contains(button.Ancestors(), element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))
                == "SecuritySettingsSection");
    }

    [Fact]
    public void TwoFactorPage_ExposesTheWiredWebAuthnManagementAction()
    {
        var parent = LoadCodeBehind();
        var child = LoadCodeBehind("SettingsTwoFactorPage.xaml.cs");
        Assert.Contains("new SettingsTwoFactorPage()", parent, StringComparison.Ordinal);
        Assert.Contains("ShowManageWebAuthnDialogAsync(page.ApplyWebAuthnSnapshot, addOnly: true)", parent, StringComparison.Ordinal);
        Assert.Contains("_manageWebAuthn = manageWebAuthn", child, StringComparison.Ordinal);
        Assert.Contains("_webAuthnSnapshot?.Credentials.Count", child, StringComparison.Ordinal);
        Assert.Contains("\"添加…\", _manageWebAuthn!", child, StringComparison.Ordinal);
        Assert.Contains("_removeWebAuthn!(credential.Id)", child, StringComparison.Ordinal);
        Assert.Contains("WebAuthnRows.Children.Add", child, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(button", child, StringComparison.Ordinal);
        Assert.Contains("button.Click += async (_, _) => await RunActionAsync(action)", child, StringComparison.Ordinal);
    }

    [Fact]
    public void TotpSetup_RendersLocalQrAndGatesSecretCopyByTheOriginalSession()
    {
        var source = LoadCodeBehind("TotpSetupCredentialsPanel.cs");
        Assert.Contains("TotpSetupQrImage", source, StringComparison.Ordinal);
        Assert.Contains("TotpSetupSecretText", source, StringComparison.Ordinal);
        Assert.Contains("TotpSetupCopySecretButton", source, StringComparison.Ordinal);
        Assert.Contains("TotpQrCodeRenderer.RenderAsync(otpauthUri", source, StringComparison.Ordinal);
        Assert.Contains("_lifecycle.TryRunSensitive", source, StringComparison.Ordinal);
        Assert.Contains("_session.TryRunIfUnlocked(_expectedSession", source, StringComparison.Ordinal);
        Assert.Contains("_clipboard.SetSecretText(_secret)", source, StringComparison.Ordinal);
        Assert.Contains("_qrImage.Source = null", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LaunchUriAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TotpSetupOtpauthText", LoadCodeBehind(), StringComparison.Ordinal);
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
        var source = LoadCodeBehind() + LoadCodeBehind("SettingsTwoFactorPage.xaml.cs");
        Assert.Contains("GetTwoFactorProviderDisplayName", source, StringComparison.Ordinal);
        Assert.DoesNotContain("4 => \"FIDO2/WebAuthn", source, StringComparison.Ordinal);
        Assert.DoesNotContain("5 => \"Duo", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SettingsTwoFactorPage.xaml.cs", "LoadProvidersAsync")]
    [InlineData("SettingsPage.xaml.cs", "BeginTotpSetupAsync")]
    [InlineData("SettingsPage.xaml.cs", "EnableTotpAsync")]
    [InlineData("SettingsPage.xaml.cs", "SendEmailAsync")]
    [InlineData("SettingsPage.xaml.cs", "EnableEmailAsync")]
    [InlineData("SettingsPage.xaml.cs", "DisableTwoFactorAsync")]
    public void TwoFactorPages_KeepTheViewModelOperationsWired(string file, string operation)
    {
        Assert.Contains(operation, LoadCodeBehind(file), StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_HasShowWebsiteIconsToggle()
    {
        Assert.Contains(LoadXaml().Descendants(), element => element.Name.LocalName == "ToggleSwitch"
            && ((string?)element.Attribute("IsOn"))?.Contains("ViewModel.ShowWebsiteIcons", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void SettingsControls_HaveAccessibleNames()
    {
        var controls = LoadXaml().Descendants()
            .Where(element => element.Name.LocalName is "ComboBox" or "ToggleSwitch").ToList();
        Assert.NotEmpty(controls);
        Assert.All(controls, control => Assert.False(string.IsNullOrWhiteSpace(
            (string?)control.Attribute("AutomationProperties.Name"))));
    }

    [Fact]
    public void SettingsComboBoxes_KeepCompactRowSizingWithoutFixedWidth()
    {
        var comboBoxes = LoadXaml().Descendants()
            .Where(element => element.Name.LocalName == "ComboBox").ToList();
        Assert.NotEmpty(comboBoxes);
        Assert.All(comboBoxes, comboBox =>
        {
            Assert.Null(comboBox.Attribute("Width"));
            Assert.InRange(double.Parse((string?)comboBox.Attribute("MinWidth") ?? "0", CultureInfo.InvariantCulture), 0, 180);
            Assert.Contains(comboBox.Ancestors(), element => element.Name.LocalName == "SettingsCard");
        });
    }

    [Fact]
    public void SettingsPage_ExposesSectionAndGroupHeadingHierarchy()
    {
        var document = LoadXaml();
        var sectionTitle = document.Descendants().Single(element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "SectionTitle");
        var groupStyle = document.Descendants().Single(element => element.Name.LocalName == "Style"
            && (string?)element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == "SettingsGroupTitleStyle");
        Assert.Equal("Level1", (string?)sectionTitle.Attribute("AutomationProperties.HeadingLevel"));
        Assert.Contains(groupStyle.Elements(), setter =>
            (string?)setter.Attribute("Property") == "AutomationProperties.HeadingLevel"
            && (string?)setter.Attribute("Value") == "Level2");
        Assert.NotEmpty(document.Descendants().Where(element => element.Name.LocalName == "TextBlock"
            && (string?)element.Attribute("Style") == "{StaticResource SettingsGroupTitleStyle}"));
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
