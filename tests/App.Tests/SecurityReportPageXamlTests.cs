using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class SecurityReportPageXamlTests
{
    private static string ReadView(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", file);
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(file);
    }

    private static string XamlText() => ReadView("SecurityReportPage.xaml");
    private static XDocument LoadXaml() => XDocument.Parse(XamlText());

    [Fact]
    public void Page_BindsProjectedPrioritiesGroupsAndExplicitExposureCommand()
    {
        var xaml = XamlText();
        Assert.Contains("ViewModel.PriorityIssue", xaml);
        Assert.Contains("ViewModel.IssueGroups", xaml);
        Assert.Contains("ViewModel.ResolvedItems", xaml);
        Assert.Contains("ViewModel.RunExposedCheckCommand", xaml);
        Assert.DoesNotContain("{Binding ", xaml);
    }

    [Fact]
    public void Page_UsesCategoryTitleAndSummaryOutsideConditionalContent()
    {
        var doc = LoadXaml();
        var contentPanel = doc.Descendants().Single(element => element.Attributes().Any(
            attribute => attribute.Name.LocalName == "Name" && attribute.Value == "OfflineContentPanel"));
        var title = Assert.Single(doc.Descendants(), element =>
            element.Attribute("Text")?.Value.Contains("ViewModel.PageTitle") == true);
        Assert.DoesNotContain(title, contentPanel.Descendants());
        Assert.Contains("ViewModel.CategorySummary", XamlText());
    }

    [Fact]
    public void ExposureNotice_RemainsVisibleUntilTheCurrentSnapshotIsConfirmed()
    {
        var doc = LoadXaml();
        var notice = doc.Descendants().Single(element => element.Attributes().Any(
            attribute => attribute.Name.LocalName == "Name" && attribute.Value == "ExposureNotice"));
        Assert.Contains("ViewModel.ExposedChecked", notice.Attribute("Visibility")?.Value);
        Assert.Contains("InverseBoolToVis", notice.Attribute("Visibility")?.Value);
        Assert.Contains("ViewModel.ExposureStatusMessage", notice.ToString());
        Assert.Contains("ViewModel.IsCheckingExposed", notice.ToString());
    }

    [Fact]
    public void AsyncStatus_IsAccessible()
    {
        var xaml = XamlText();
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"安全报告分析进度\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"正在检查暴露密码\"", xaml);
    }

    [Fact]
    public void ReportActions_AdaptToAvailableContentWidth()
    {
        var code = ReadView("SecurityReportPage.xaml.cs");
        Assert.Contains("OnIssueRowSizeChanged", XamlText());
        Assert.Contains("Grid.SetRow(actions", code);
        Assert.Contains("Grid.SetColumnSpan(actions", code);
    }

    [Fact]
    public void ExposureCheckAndFailure_KeepStableAutomationIdentities()
    {
        var doc = LoadXaml();
        var action = Assert.Single(doc.Descendants(), element =>
            element.Attribute("AutomationProperties.AutomationId")?.Value == "RunExposedCheckButton");
        Assert.Contains("RunExposedCheckCommand", action.Attribute("Command")?.Value);
        var error = Assert.Single(doc.Descendants(), element =>
            element.Attribute("AutomationProperties.AutomationId")?.Value == "ExposedErrorInfoBar");
        Assert.Contains("ViewModel.HasExposedError", error.Attribute("IsOpen")?.Value);
        Assert.Contains("ViewModel.ExposedError", error.Attribute("Message")?.Value);
    }

    [Fact]
    public void EveryFindingOpensItsActualItemWithoutBindingSecretValues()
    {
        var xaml = XamlText();
        var actions = LoadXaml().Descendants().Where(element =>
            element.Name.LocalName == "Button" && element.Attribute("Click")?.Value == "OnOpenItemClick").ToArray();
        Assert.Equal(2, actions.Length);
        Assert.All(actions, action => Assert.Contains("CipherId", action.Attribute("Tag")?.Value));
        Assert.DoesNotContain("GeneratedPassword", xaml);
        Assert.DoesNotContain("PasswordHash", xaml);
        Assert.Contains("vault:item:", ReadView("SecurityReportPage.xaml.cs"));
    }

    [Fact]
    public void FixAction_SeparatesPasswordReplacementFromHttpEditing()
    {
        var code = ReadView("SecurityReportPage.xaml.cs");
        Assert.Contains("row.Category == \"http\"", code);
        Assert.Contains("vault:edit:", code);
        Assert.Contains("vault:change-password:", code);
        Assert.Contains("OnFixItemClick", XamlText());
    }
}
