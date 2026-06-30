using System.Xml.Linq;
using Xunit;

namespace App.Tests;

/// <summary>
/// Task 6 XAML 结构测试：
/// 1. RegisterPage.xaml 包含所需的输入控件及 AutomationId
/// 2. RegisterPage 使用 x:DataType="vm:RegisterViewModel"（通过 xmlns:vm 声明验证）
/// 3. LoginPage.xaml 包含"没有账户？注册" HyperlinkButton (GoToRegisterButton)
/// </summary>
public class RegisterPageXamlTests
{
    private static XDocument LoadRegisterPage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "RegisterPage.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("RegisterPage.xaml not found.");
    }

    private static XDocument LoadLoginPage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "LoginPage.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("LoginPage.xaml not found.");
    }

    private static bool HasAutomationId(XDocument doc, string automationId) =>
        doc.Descendants().Any(e =>
            e.Attributes().Any(a =>
                a.Name.LocalName == "AutomationProperties.AutomationId"
                && a.Value == automationId));

    // ── RegisterPage AutomationId 断言 ───────────────────────────────────────

    [Fact]
    public void RegisterPage_HasRegisterSubmitButton()
    {
        var doc = LoadRegisterPage();
        Assert.True(HasAutomationId(doc, "RegisterSubmitButton"),
            "RegisterPage.xaml must have AutomationProperties.AutomationId=\"RegisterSubmitButton\"");
    }

    [Fact]
    public void RegisterPage_HasBackToLoginButton()
    {
        var doc = LoadRegisterPage();
        Assert.True(HasAutomationId(doc, "BackToLoginButton"),
            "RegisterPage.xaml must have AutomationProperties.AutomationId=\"BackToLoginButton\"");
    }

    // ── RegisterPage 输入控件断言 ─────────────────────────────────────────────

    [Fact]
    public void RegisterPage_HasServerComboBox()
    {
        var doc = LoadRegisterPage();
        var comboBox = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ComboBox"
                && e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.AutomationId"
                    && a.Value == "RegisterServerOptionBox"));
        Assert.NotNull(comboBox);
    }

    [Fact]
    public void RegisterPage_HasServerUrlTextBox()
    {
        var doc = LoadRegisterPage();
        Assert.True(HasAutomationId(doc, "RegisterServerUrlTextBox"),
            "RegisterPage.xaml must have a self-host server URL TextBox.");
    }

    [Fact]
    public void RegisterPage_HasEmailTextBox()
    {
        var doc = LoadRegisterPage();
        Assert.True(HasAutomationId(doc, "RegisterEmailTextBox"),
            "RegisterPage.xaml must have an Email TextBox.");
    }

    [Fact]
    public void RegisterPage_HasNameTextBox()
    {
        var doc = LoadRegisterPage();
        Assert.True(HasAutomationId(doc, "RegisterNameTextBox"),
            "RegisterPage.xaml must have a Name TextBox.");
    }

    [Fact]
    public void RegisterPage_HasMasterPasswordBox()
    {
        var doc = LoadRegisterPage();
        Assert.True(HasAutomationId(doc, "RegisterMasterPasswordBox"),
            "RegisterPage.xaml must have a MasterPassword PasswordBox.");
    }

    [Fact]
    public void RegisterPage_HasConfirmPasswordBox()
    {
        var doc = LoadRegisterPage();
        Assert.True(HasAutomationId(doc, "RegisterConfirmPasswordBox"),
            "RegisterPage.xaml must have a ConfirmPassword PasswordBox.");
    }

    [Fact]
    public void RegisterPage_HasMasterPasswordHintTextBox()
    {
        var doc = LoadRegisterPage();
        Assert.True(HasAutomationId(doc, "RegisterMasterPasswordHintTextBox"),
            "RegisterPage.xaml must have a MasterPasswordHint TextBox.");
    }

    // ── RegisterPage x:Bind / RegisterViewModel 命名空间断言 ─────────────────

    [Fact]
    public void RegisterPage_HasViewModelNamespaceDeclaration()
    {
        var doc = LoadRegisterPage();
        // 检查根元素声明了 xmlns:vm="using:App.ViewModels"
        var root = doc.Root;
        Assert.NotNull(root);
        var vmNs = root.Attributes()
            .FirstOrDefault(a => a.Name.LocalName == "vm" && a.Value.Contains("App.ViewModels"));
        Assert.NotNull(vmNs);
    }

    [Fact]
    public void RegisterPage_HasXBindToViewModel()
    {
        var doc = LoadRegisterPage();
        // 至少有一个 x:Bind 引用了 ViewModel. （说明 code-behind 暴露了 ViewModel 属性）
        var hasViewModelBind = doc.Descendants()
            .SelectMany(e => e.Attributes())
            .Any(a => a.Value.StartsWith("{x:Bind ViewModel.", StringComparison.Ordinal));
        Assert.True(hasViewModelBind,
            "RegisterPage.xaml must use x:Bind ViewModel.* bindings.");
    }

    [Fact]
    public void RegisterPage_HasErrorInfoBar()
    {
        var doc = LoadRegisterPage();
        var infoBar = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "InfoBar"
                && e.Attributes().Any(a => a.Name.LocalName == "Message"
                    && a.Value.Contains("ViewModel.OperationError")));
        Assert.NotNull(infoBar);
    }

    // ── LoginPage GoToRegisterButton 断言 ────────────────────────────────────

    [Fact]
    public void LoginPage_HasGoToRegisterButton()
    {
        var doc = LoadLoginPage();
        Assert.True(HasAutomationId(doc, "GoToRegisterButton"),
            "LoginPage.xaml must have AutomationProperties.AutomationId=\"GoToRegisterButton\"");
    }

    [Fact]
    public void LoginPage_GoToRegisterButton_IsHyperlinkButton()
    {
        var doc = LoadLoginPage();
        var btn = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "HyperlinkButton"
                && e.Attributes().Any(a =>
                    a.Name.LocalName == "AutomationProperties.AutomationId"
                    && a.Value == "GoToRegisterButton"));
        Assert.NotNull(btn);
    }
}
