using System.Xml.Linq;
using Xunit;

namespace App.Tests;

/// <summary>
/// </summary>
public class SecurityReportPageXamlTests
{
    private static string XamlText()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "SecurityReportPage.xaml");
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find SecurityReportPage.xaml from the test output directory.");
    }

    private static XDocument LoadXaml() => XDocument.Parse(XamlText());

    [Fact]
    public void Page_BindsSectionsAndExposedCommand()
    {
        var xaml = XamlText();
        Assert.Contains("WeakItems", xaml);
        Assert.Contains("ReusedGroups", xaml);
        Assert.Contains("UnsecuredItems", xaml);
        Assert.Contains("ExposedItems", xaml);
        Assert.Contains("RunExposedCheckCommand", xaml);
        Assert.DoesNotContain("{Binding ", xaml); // AOT — no reflection bindings
    }

    [Fact]
    public void Nav_HasReportsEntry()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "App", "MainWindow.xaml")))
            dir = dir.Parent;
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src", "App", "MainWindow.xaml"));
        Assert.Contains("Tag=\"reports\"", xaml);
    }

    [Fact]
    public void Page_UsesNativeExpandersForEveryReportCategory()
    {
        var doc = LoadXaml();
        var count = doc.Descendants().Count(e => e.Name.LocalName == "Expander");
        Assert.Equal(4, count);
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "SettingsExpander");
    }

    [Fact]
    public void Page_HasAtLeastFourInfoBadges()
    {
        var doc = LoadXaml();
        var count = doc.Descendants().Count(e => e.Name.LocalName == "InfoBadge");
        Assert.True(count >= 4, $"Expected >= 4 InfoBadge elements, found {count}.");
    }

    [Fact]
    public void HeroCard_BindsSummaryText()
    {
        var xaml = XamlText();
        Assert.Contains("ViewModel.SummaryText", xaml);
    }

    [Fact]
    public void HeroCard_BindsOverallSeverityWithConverter()
    {
        var xaml = XamlText();
        Assert.Contains("ViewModel.OverallSeverity", xaml);
        Assert.Contains("SeverityToBrush", xaml);
    }

    [Fact]
    public void PageTitle_IsOutsideConditionalContentPanel()
    {
        var doc = LoadXaml();
        var contentPanel = doc.Descendants().Single(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "OfflineContentPanel"));

        Assert.DoesNotContain(contentPanel.Descendants(), e =>
            e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "安全报告");
        Assert.Single(doc.Descendants().Where(e =>
            e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "安全报告"));
    }

    [Fact]
    public void ExposureMetric_ShowsStatusUntilCountIsConfirmed()
    {
        var xaml = XamlText();

        Assert.Contains("ViewModel.ShowExposedCount", xaml);
        Assert.Contains("ViewModel.ExposureStatusLabel", xaml);
        Assert.Contains("ViewModel.ExposureMetricAccessibleName", xaml);
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
    public void HeroCard_HasNarrowWindowLayout()
    {
        var xaml = XamlText();

        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"0\"", xaml);
        Assert.Contains("HealthMetricsPanel.(Grid.Row)", xaml);
        Assert.Contains("HealthMetricsPanel.(Grid.ColumnSpan)", xaml);
    }

    [Fact]
    public void RunExposedCheckButton_UsesAccentButtonStyle()
    {
        var doc = LoadXaml();
        var button = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Button"
                && e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.AutomationId"
                    && a.Value == "RunExposedCheckButton"));
        Assert.NotNull(button);
        Assert.Equal("{ThemeResource AccentButtonStyle}", (string?)button!.Attribute("Style"));
    }

    [Fact]
    public void ExposedErrorInfoBar_AutomationIdPreserved()
    {
        var doc = LoadXaml();
        var infoBar = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "InfoBar"
                && e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.AutomationId"
                    && a.Value == "ExposedErrorInfoBar"));
        Assert.NotNull(infoBar);
    }

    [Fact]
    public void EveryFindingOpensTheActualItem_WithoutDisplayingPasswords()
    {
        var xaml = XamlText();
        var doc = LoadXaml();
        var actions = doc.Descendants().Where(e => e.Name.LocalName == "Button"
            && (string?)e.Attribute("Click") == "OnOpenItemClick").ToArray();
        Assert.Equal(4, actions.Length);
        Assert.All(actions, button => Assert.Contains("CipherId", (string?)button.Attribute("Tag")));
        Assert.DoesNotContain("{x:Bind Score}", xaml);
        Assert.DoesNotContain("{x:Bind BreachCount}", xaml);
    }
}
