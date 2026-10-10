using System.Xml.Linq;
using System.Collections.ObjectModel;
using App.Services;
using App.ViewModels;
using Xunit;

namespace App.Tests;

public class DemoVaultLoginWiringTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void LoginPage_HasDebugDemoVaultButtonBoundToCommand()
    {
        var document = LoadXaml("LoginPage.xaml");
        var button = RequireByName(document, "DemoVaultButton");

        Assert.Equal("使用演示保险库", button.Attribute("Content")?.Value);
        Assert.Contains("UseDemoVaultCommand", button.Attribute("Command")?.Value);
        Assert.Contains("CanUseDemoVault", button.Attribute("Visibility")?.Value);
        Assert.Equal("Stretch", button.Attribute("HorizontalAlignment")?.Value);
    }

    [Fact]
    public void LoginPage_HasNativeWizardStageSurfaces()
    {
        var document = LoadXaml("LoginPage.xaml");

        Assert.Contains("IsAccountStage", RequireElementByName(document, "LoginAccountStage").Attribute("Visibility")?.Value);
        Assert.Contains("IsPasswordStage", RequireElementByName(document, "LoginPasswordStage").Attribute("Visibility")?.Value);
        Assert.Contains("IsDeviceVerificationStage", RequireElementByName(document, "LoginDeviceVerificationStage").Attribute("Visibility")?.Value);
        Assert.Contains("IsPasswordChangeRequiredStage", RequireElementByName(document, "LoginPasswordChangeRequiredStage").Attribute("Visibility")?.Value);
        Assert.Contains("IsTwoFactorStage", RequireElementByName(document, "LoginTwoFactorStage").Attribute("Visibility")?.Value);
        Assert.Contains("DeviceVerificationCode", RequireElementByName(document, "DeviceVerificationCodeTextBox").Attribute("Text")?.Value);
        Assert.Contains("ResendNewDeviceCodeCommand", RequireElementByName(document, "ResendNewDeviceCodeButton").Attribute("Command")?.Value);
        Assert.Contains("NewMasterPassword", RequireElementByName(document, "NewMasterPasswordBox").Attribute("Password")?.Value);
        Assert.Contains("ConfirmNewMasterPassword", RequireElementByName(document, "ConfirmNewMasterPasswordBox").Attribute("Password")?.Value);
        Assert.Contains("ShowUnlockPanel", RequireElementByName(document, "LoginUnlockStage").Attribute("Visibility")?.Value);
        Assert.Contains("SelectedServerOptionIndex", RequireElementByName(document, "ServerOptionBox").Attribute("SelectedIndex")?.Value);
        var statusBar = RequireElementByName(document, "LoginStatusInfoBar");
        Assert.Contains("HasStatus", statusBar.Attribute("IsOpen")?.Value);
        Assert.Contains("StatusKind", statusBar.Attribute("Severity")?.Value);
        Assert.Contains("LoginStatusKindToInfoBarSeverityConverter", statusBar.Attribute("Severity")?.Value);

        var primary = RequireElementByName(document, "PrimaryAuthButton");
        Assert.Equal("OnLogin", primary.Attribute("Click")?.Value);
        Assert.Contains("CanUsePrimaryAction", primary.Attribute("IsEnabled")?.Value);

        Assert.Contains("BackCommand", RequireElementByName(document, "BackButton").Attribute("Command")?.Value);
        Assert.Contains("SwitchAccountCommand", RequireElementByName(document, "SwitchAccountButton").Attribute("Command")?.Value);
        Assert.Equal("0", RequireElementByName(document, "LoginForm").Attribute("Spacing")?.Value);
        Assert.Equal("0,28,0,0", RequireElementByName(document, "LoginAccountStage").Attribute("Margin")?.Value);
        Assert.Equal("32", primary.Attribute("MinHeight")?.Value);
    }

    [Fact]
    public void LoginPage_WiresYubiKeyLengthAndRecoveryCodeWarning()
    {
        var document = LoadXaml("LoginPage.xaml");
        var code = RequireElementByName(document, "TwoFactorCodeTextBox");
        var warning = RequireElementByName(document, "RecoveryCodeWarningInfoBar");
        var switchButton = RequireElementByName(document, "RecoveryCodeModeButton");

        Assert.Equal("128", code.Attribute("MaxLength")?.Value);
        Assert.Contains("TwoFactorInputHeader", code.Attribute("Header")?.Value);
        Assert.Contains("TwoFactorInputPlaceholder", code.Attribute("PlaceholderText")?.Value);
        Assert.Contains("ShowTwoFactorCodeInput", code.Attribute("Visibility")?.Value);
        Assert.Equal("Warning", warning.Attribute("Severity")?.Value);
        Assert.Contains("ShowRecoveryCodeWarning", warning.Attribute("IsOpen")?.Value);
        Assert.Equal("OnOpenTwoFactorPickerClick", switchButton.Attribute("Click")?.Value);
        Assert.Contains("CanUsePrimaryAction", switchButton.Attribute("IsEnabled")?.Value);
        Assert.Equal("360", RequireElementByName(document, "TwoFactorPickerPanel").Attribute("MaxWidth")?.Value);
        var source = LoadSource("LoginPage.xaml.cs", "src", "App", "Views");
        Assert.Contains("ViewModel.ToggleRecoveryCodeModeCommand.Execute", source);
        Assert.Contains("ViewModel.SelectedTwoFactorProviderIndex =", source);
    }

    [Fact]
    public void LoginPage_ExposesSavedAccountPickerAndPinStageSwitchAction()
    {
        var document = LoadXaml("LoginPage.xaml");
        var accounts = RequireElementByName(document, "SavedAccountsPanel");
        var pinSwitch = RequireElementByName(document, "PinSwitchAccountButton");

        Assert.Contains("ShowSavedAccounts", accounts.Attribute("Visibility")?.Value);
        Assert.Contains(accounts.Descendants(),
            element => element.Attribute("Click")?.Value == "OnSavedAccountClick");
        Assert.Contains("SwitchAccountCommand", pinSwitch.Attribute("Command")?.Value);
    }

    [Fact]
    public void LoginPage_ExposesTrustedDeviceApprovalFingerprintAndCancellation()
    {
        var document = LoadXaml("LoginPage.xaml");
        var stage = RequireElementByName(document, "LoginTrustedDeviceApprovalStage");
        var fingerprint = RequireElementByName(document, "DeviceApprovalFingerprintBox");

        Assert.Contains("IsTrustedDeviceApprovalStage", stage.Attribute("Visibility")?.Value);
        Assert.Equal("True", fingerprint.Attribute("IsReadOnly")?.Value);
        Assert.Contains("ApprovalFingerprint", fingerprint.Attribute("Text")?.Value);
        Assert.Contains(
            stage.Descendants(),
            element => element.Attribute("Command")?.Value?.Contains(
                "CancelTrustedDeviceApprovalCommand",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            stage.Descendants(),
            element => element.Attribute("IsChecked")?.Value?.Contains(
                "TrustCurrentDeviceAfterApproval",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void LoginPage_ItemsSourcesUseNativeAotCompatibleCollections()
    {
        Assert.Equal(
            typeof(ObservableCollection<TrustedDeviceApprovalMethodOption>),
            typeof(LoginViewModel).GetProperty(nameof(LoginViewModel.ApprovalMethodOptions))!.PropertyType);
        Assert.Equal(
            typeof(ObservableCollection<TwoFactorProviderOption>),
            typeof(LoginViewModel).GetProperty(nameof(LoginViewModel.TwoFactorProviderOptions))!.PropertyType);
        Assert.Equal(
            typeof(ObservableCollection<SavedAccountOption>),
            typeof(LoginViewModel).GetProperty(nameof(LoginViewModel.SavedAccounts))!.PropertyType);
    }

    [Fact]
    public void LoginPage_WiresSsoStartAndCancelActions()
    {
        var document = LoadXaml("LoginPage.xaml");
        var start = RequireElementByName(document, "SsoLoginButton");
        var cancel = RequireElementByName(document, "CancelSsoButton");
        var register = RequireElementByName(document, "GoToRegisterButton");

        Assert.Equal("OnOpenSsoSignInClick", start.Attribute("Click")?.Value);
        Assert.Equal("360", RequireElementByName(document, "SsoSignInPanel").Attribute("MaxWidth")?.Value);
        Assert.Contains("SsoOrganizationIdentifier", RequireElementByName(document, "SsoOrganizationBox").Attribute("Text")?.Value);
        Assert.Equal("OnSsoReopenClick", RequireElementByName(document, "SsoReopenButton").Attribute("Click")?.Value);
        Assert.Contains("CanUseSso", start.Attribute("IsEnabled")?.Value);
        Assert.Contains("CancelSsoCommand", cancel.Attribute("Command")?.Value);
        Assert.Contains("ShowCancelSso", cancel.Attribute("Visibility")?.Value);
        Assert.Contains("CanEditServer", register.Attribute("IsEnabled")?.Value);
    }

    [Fact]
    public void MainWindow_KeepsAuthenticationInTheStableShellWithScrollableContent()
    {
        var source = LoadSource("MainWindow.xaml.cs", "src", "App");
        var fitStart = source.IndexOf("public void FitLoginWindowToContent(", StringComparison.Ordinal);
        Assert.True(fitStart >= 0);
        var fitEnd = source.IndexOf("\n    private ", fitStart, StringComparison.Ordinal);
        Assert.True(fitEnd > fitStart);
        var fitMethod = source[fitStart..fitEnd];

        Assert.Contains("DefaultVaultWindowWidthDip = 1280", source);
        Assert.Contains("DefaultVaultWindowHeightDip = 760", source);
        Assert.Contains("RequestLoginWindowFit", source);
        Assert.Contains("ApplyLoginWindowLayout()", source);
        Assert.Contains("ApplyVaultWindowLayout()", source);
        Assert.Contains("Activated += OnFirstActivated", source);
        Assert.Contains("RequestLoginWindowFit();", source);
        Assert.Contains("if (!_isLoginWindow || loginContent.XamlRoot is null)", fitMethod);
        Assert.Contains("if (ShellRoot.ActualWidth is > 0 and < 640)", fitMethod);
        Assert.Contains("ResizeClientDip(640, Math.Max(480, ShellRoot.ActualHeight))", fitMethod);
        Assert.DoesNotContain("loginContent.Measure", fitMethod);
        Assert.DoesNotContain("loginContent.DesiredSize", fitMethod);
        Assert.DoesNotContain("ResizeLoginClientDip", fitMethod);

        var document = LoadXaml("LoginPage.xaml");
        var scrollViewer = RequireElementByName(document, "LoginScrollViewer");
        Assert.Equal("Auto", scrollViewer.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.Equal("Disabled", scrollViewer.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.Equal("360", RequireElementByName(document, "LoginForm").Attribute("MaxWidth")?.Value);
        Assert.Contains(RequireElementByName(document, "LoginForm"), scrollViewer.Descendants());
    }

    [Fact]
    public void HelloEnrollment_PreservesSkipAndExactSessionCommitBoundaries()
    {
        var document = LoadXaml("LoginPage.xaml");
        var source = LoadSource("LoginPage.xaml.cs", "src", "App", "Views");
        var window = LoadSource("MainWindow.xaml.cs", "src", "App");
        Assert.Equal("Collapsed", RequireElementByName(document, "HelloEnrollmentPanel").Attribute("Visibility")?.Value);
        Assert.Equal("OnSkipHelloEnrollmentClick", RequireElementByName(document, "SkipHelloEnrollmentButton").Attribute("Click")?.Value);
        Assert.Equal("OnEnableHelloEnrollmentClick", RequireElementByName(document, "EnableHelloEnrollmentButton").Attribute("Click")?.Value);
        Assert.Contains("window.ShowHelloEnrollment(lease", source);
        Assert.Contains("ShowHelloEnrollment(VaultSessionLease lease, Action showEnrollment)", window);
        Assert.Contains("lifecycle.TryRunSensitive(() => session.TryRunIfUnlocked(lease", source);
        Assert.Contains("IsCurrentHelloEnrollment(lease, generation, cancellation)", source);
        Assert.Contains("CryptographicOperations.ZeroMemory(signature)", source);
        Assert.Contains("RequestIfAsync(AppLifecycleIntent.Lock, () => session.IsUnlocked(lease))", source);
        Assert.Contains("session.UsesV2UserKey", source);
    }

    [Fact]
    public void LoginPage_RequestsWindowFitWhenLayoutAffectingStateChanges()
    {
        var source = LoadSource("LoginPage.xaml.cs", "src", "App", "Views");

        Assert.Contains("ViewModel.PropertyChanged += OnViewModelPropertyChanged", source);
        Assert.Contains("LoginForm.SizeChanged += OnLoginFormSizeChanged", source);
        Assert.Contains("RequestWindowFit", source);
        Assert.Contains("QueueFitWindowToContent", source);
        Assert.Contains("FitLoginWindowToContent(LoginForm, LoginSurface.Padding)", source);
        Assert.Contains("IsLayoutAffectingProperty", source);
    }

    [Fact]
    public void ServiceConfiguration_RegistersDemoVaultSessionServiceInDebug()
    {
        var source = LoadSource("ServiceConfiguration.cs", "src", "App", "Services");

        Assert.Contains("#if DEBUG", source);
        Assert.Contains("AddSingleton<IDemoVaultSessionService, DemoVaultSessionService>()", source);
    }

    private static XElement RequireByName(XDocument document, string name)
    {
        var element = document.Descendants(Xaml + "Button")
            .FirstOrDefault(e => e.Attribute(X + "Name")?.Value == name);
        if (element is null)
        {
            Assert.Fail($"Expected x:Name='{name}' in LoginPage.xaml.");
            throw new InvalidOperationException($"Expected x:Name='{name}' in LoginPage.xaml.");
        }

        return element;
    }

    private static XElement RequireElementByName(XDocument document, string name)
    {
        var element = document.Descendants()
            .FirstOrDefault(e => e.Attribute(X + "Name")?.Value == name);
        if (element is null)
        {
            Assert.Fail($"Expected x:Name='{name}' in LoginPage.xaml.");
            throw new InvalidOperationException($"Expected x:Name='{name}' in LoginPage.xaml.");
        }

        return element;
    }

    private static XDocument LoadXaml(string fileName) =>
        XDocument.Load(FindRepoFile("src", "App", "Views", fileName));

    private static string LoadSource(string fileName, params string[] directoryParts) =>
        File.ReadAllText(FindRepoFile(directoryParts.Concat([fileName]).ToArray()));

    private static string FindRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(path))
                return path;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(parts)} from the test output directory.");
    }
}
