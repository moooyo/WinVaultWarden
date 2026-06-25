using System.Xml.Linq;
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
        Assert.Contains("IsTwoFactorStage", RequireElementByName(document, "LoginTwoFactorStage").Attribute("Visibility")?.Value);
        Assert.Contains("IsUnlockStage", RequireElementByName(document, "LoginUnlockStage").Attribute("Visibility")?.Value);
        Assert.Contains("SelectedServerOptionIndex", RequireElementByName(document, "ServerOptionBox").Attribute("SelectedIndex")?.Value);
        Assert.Contains("HasStatus", RequireElementByName(document, "LoginStatusInfoBar").Attribute("IsOpen")?.Value);

        var primary = RequireElementByName(document, "PrimaryAuthButton");
        Assert.Equal("OnLogin", primary.Attribute("Click")?.Value);
        Assert.Contains("CanUsePrimaryAction", primary.Attribute("IsEnabled")?.Value);

        Assert.Contains("BackCommand", RequireElementByName(document, "BackButton").Attribute("Command")?.Value);
        Assert.Contains("SwitchAccountCommand", RequireElementByName(document, "SwitchAccountButton").Attribute("Command")?.Value);
        Assert.Equal("0", RequireElementByName(document, "LoginForm").Attribute("Spacing")?.Value);
        Assert.Equal("0,18,0,0", RequireElementByName(document, "LoginAccountStage").Attribute("Margin")?.Value);
    }

    [Fact]
    public void MainWindow_AdaptsLoginWindowToMeasuredContent()
    {
        var source = LoadSource("MainWindow.xaml.cs", "src", "App");

        Assert.Contains("LoginWindowMeasureWidthDip", source);
        Assert.Contains("LoginWindowMinWidthDip", source);
        Assert.Contains("LoginWindowMaxHeightDip", source);
        Assert.Contains("FitLoginWindowToContent", source);
        Assert.Contains("RequestLoginWindowFit", source);
        Assert.Contains("loginContent.Measure", source);
        Assert.Contains("loginContent.DesiredSize.Height", source);
        Assert.Contains("contentPadding", source);
        Assert.Contains("ResizeClientDip", source);
        Assert.Contains("AppWindow.ResizeClient", source);
        Assert.Contains("DisplayArea.GetFromWindowId", source);
        Assert.Contains("Math.Clamp", source);
        Assert.Contains("ApplyLoginWindowLayout()", source);
        Assert.Contains("ApplyVaultWindowLayout()", source);
        Assert.Contains("ResizeWindowDip", source);
        Assert.Contains("Activated += OnFirstActivated", source);
        Assert.Contains("RequestLoginWindowFit();", source);
        Assert.DoesNotContain("Math.Max(loginContent.ActualHeight", source);
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
