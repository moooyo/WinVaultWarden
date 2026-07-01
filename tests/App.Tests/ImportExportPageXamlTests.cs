using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class ImportExportPageXamlTests
{
    private static string RepoRootFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return path;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"{relative} not found.");
    }

    private static XDocument LoadXaml() => XDocument.Load(RepoRootFile(Path.Combine("src", "App", "Views", "ImportExportPage.xaml")));

    private static string LoadMainWindowCode() => File.ReadAllText(RepoRootFile(Path.Combine("src", "App", "MainWindow.xaml.cs")));

    // ── 导出区 ───────────────────────────────────────────────────────────────

    [Fact]
    public void ExportSection_HasWarningInfoBar()
    {
        var doc = LoadXaml();
        var infoBar = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "InfoBar"
            && e.Attribute("Severity")?.Value == "Warning");
        Assert.NotNull(infoBar);
    }

    [Fact]
    public void ExportSection_HasFormatComboBox_BoundToExportFormatIndex()
    {
        var doc = LoadXaml();
        var combo = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ComboBox"
            && e.Attribute("AutomationProperties.AutomationId")?.Value == "ExportFormatCombo");
        Assert.NotNull(combo);
        Assert.Contains("ExportFormatIndex", combo!.Attribute("SelectedIndex")?.Value ?? string.Empty);
    }

    [Fact]
    public void ExportSection_HasExportButton_WithClickHandler()
    {
        var doc = LoadXaml();
        var button = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Button"
            && e.Attribute("AutomationProperties.AutomationId")?.Value == "ExportButton");
        Assert.NotNull(button);
        Assert.Equal("OnExportClick", button!.Attribute("Click")?.Value);
    }

    [Fact]
    public void ExportSection_ResultText_BoundToExportStatus()
    {
        var doc = LoadXaml();
        var textBlock = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "TextBlock"
            && e.Attribute("AutomationProperties.AutomationId")?.Value == "ExportResultText");
        Assert.NotNull(textBlock);
        Assert.Contains("ExportStatus", textBlock!.Attribute("Text")?.Value ?? string.Empty);
    }

    // ── 导入区 ───────────────────────────────────────────────────────────────

    [Fact]
    public void ImportSection_HasChooseFileButton()
    {
        var doc = LoadXaml();
        var button = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Button"
            && e.Attribute("AutomationProperties.AutomationId")?.Value == "ImportChooseFileButton");
        Assert.NotNull(button);
        Assert.Equal("OnChooseImportFileClick", button!.Attribute("Click")?.Value);
    }

    [Fact]
    public void ImportSection_HasImportButton_BoundToDoImportCommand()
    {
        var doc = LoadXaml();
        var button = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Button"
            && e.Attribute("AutomationProperties.AutomationId")?.Value == "ImportButton");
        Assert.NotNull(button);
        Assert.Contains("DoImportCommand", button!.Attribute("Command")?.Value ?? string.Empty);
    }

    [Fact]
    public void ImportSection_ShowsPreviewCipherAndFolderCounts()
    {
        var doc = LoadXaml();
        Assert.Contains(doc.Descendants(), e => (e.Attribute("Text")?.Value ?? string.Empty).Contains("PreviewCipherCount"));
        Assert.Contains(doc.Descendants(), e => (e.Attribute("Text")?.Value ?? string.Empty).Contains("PreviewFolderCount"));
    }

    [Fact]
    public void ImportSection_ErrorInfoBar_BoundToHasImportError()
    {
        var doc = LoadXaml();
        var infoBar = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "InfoBar"
            && e.Attribute("AutomationProperties.AutomationId")?.Value == "ImportErrorInfoBar");
        Assert.NotNull(infoBar);
        Assert.Contains("HasImportError", infoBar!.Attribute("IsOpen")?.Value ?? string.Empty);
    }

    [Fact]
    public void ImportSection_ResultText_BoundToResultMessage()
    {
        var doc = LoadXaml();
        var textBlock = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "TextBlock"
            && e.Attribute("AutomationProperties.AutomationId")?.Value == "ImportResultText");
        Assert.NotNull(textBlock);
        Assert.Contains("ResultMessage", textBlock!.Attribute("Text")?.Value ?? string.Empty);
    }

    // ── 导航 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void MainWindow_IoCase_NavigatesToImportExportPage()
    {
        var code = LoadMainWindowCode();
        var ioCaseIndex = code.IndexOf("case \"io\":", StringComparison.Ordinal);
        Assert.True(ioCaseIndex >= 0, "case \"io\": not found in MainWindow.xaml.cs");

        var snippet = code.Substring(ioCaseIndex, Math.Min(200, code.Length - ioCaseIndex));
        Assert.Contains("ContentFrame.Navigate(typeof(ImportExportPage))", snippet);
    }
}
