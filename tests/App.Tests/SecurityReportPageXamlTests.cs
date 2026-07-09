using System.Xml.Linq;
using Xunit;

namespace App.Tests;

/// <summary>
/// Task 8:安全报告页改版为原生 WinUI Settings 风格(SettingsExpander + InfoBadge + Hero 卡片)。
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
    public void Page_HasAtLeastFourSettingsExpanders()
    {
        var doc = LoadXaml();
        var count = doc.Descendants().Count(e => e.Name.LocalName == "SettingsExpander");
        Assert.True(count >= 4, $"Expected >= 4 SettingsExpander elements, found {count}.");
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
    public void UsesSettingsControlsNamespace()
    {
        var xaml = XamlText();
        Assert.Contains("xmlns:tk=\"using:CommunityToolkit.WinUI.Controls\"", xaml);
    }
}
