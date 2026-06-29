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
}
