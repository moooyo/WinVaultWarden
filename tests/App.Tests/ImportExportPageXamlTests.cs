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

    private static string LoadPageCode() =>
        File.ReadAllText(RepoRootFile(Path.Combine("src", "App", "Views", "ImportExportPage.xaml.cs")));

    private static string LoadMainWindowCode() => File.ReadAllText(RepoRootFile(Path.Combine("src", "App", "MainWindow.xaml.cs")));

    private static string LoadFrameNavServiceCode() =>
        File.ReadAllText(RepoRootFile(Path.Combine("src", "App", "Services", "FrameNavigationService.cs")));

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
    public void Page_UsesUnifiedOperationStatusInfoBarsWithLiveRegions()
    {
        var doc = LoadXaml();
        var statuses = doc.Descendants()
            .Where(e => e.Name.LocalName == "InfoBar")
            .Where(e => (e.Attribute("AutomationProperties.AutomationId")?.Value ?? string.Empty)
                .StartsWith("Operation", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(4, statuses.Length);
        Assert.All(statuses, infoBar =>
        {
            Assert.Contains("OperationMessage", infoBar.Attribute("Message")?.Value ?? string.Empty);
            Assert.NotNull(infoBar.Attribute("AutomationProperties.LiveSetting"));
        });
        Assert.Contains(statuses, e => (e.Attribute("IsOpen")?.Value ?? string.Empty).Contains("HasSucceededStatus"));
        Assert.Contains(statuses, e => (e.Attribute("IsOpen")?.Value ?? string.Empty).Contains("HasWarningStatus"));
        Assert.Contains(statuses, e => (e.Attribute("IsOpen")?.Value ?? string.Empty).Contains("HasCancelledStatus"));
        Assert.Contains(statuses, e => (e.Attribute("IsOpen")?.Value ?? string.Empty).Contains("HasFailedStatus"));
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
    public void ImportPreviewPanel_VisibilityBoundToHasValidPreview()
    {
        var doc = LoadXaml();
        var panel = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "StackPanel"
            && e.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name")?.Value == "ImportPreviewPanel");
        Assert.NotNull(panel);
        var vis = panel!.Attribute("Visibility")?.Value ?? string.Empty;
        Assert.Contains("HasValidPreview", vis);
        Assert.Contains("BoolToVisibilityConverter", vis);
    }

    [Fact]
    public void ImportButton_IsEnabledBoundToCanImport()
    {
        var doc = LoadXaml();
        var button = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Button"
            && e.Attribute("AutomationProperties.AutomationId")?.Value == "ImportButton");
        Assert.NotNull(button);
        Assert.Contains("CanImport", button!.Attribute("IsEnabled")?.Value ?? string.Empty);
    }

    [Fact]
    public void Page_ShowsBusyStateInLiveRegion()
    {
        var doc = LoadXaml();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var panel = doc.Descendants().FirstOrDefault(e =>
            e.Attribute(x + "Name")?.Value == "OperationBusyPanel");
        Assert.NotNull(panel);
        Assert.Contains("IsBusy", panel!.Attribute("Visibility")?.Value ?? string.Empty);
        Assert.Contains(panel.Descendants(), e =>
            e.Attribute("AutomationProperties.LiveSetting")?.Value == "Polite"
            && (e.Attribute("Text")?.Value ?? string.Empty).Contains("BusyMessage"));
    }

    [Fact]
    public void InteractiveControls_AreDisabledBySharedBusyState()
    {
        var doc = LoadXaml();
        var ids = new[]
        {
            "ExportFormatCombo",
            "ExportButton",
            "ImportFormatCombo",
            "ImportChooseFileButton",
        };

        foreach (var id in ids)
        {
            var element = doc.Descendants().Single(e =>
                e.Attribute("AutomationProperties.AutomationId")?.Value == id);
            Assert.Contains("CanOperate", element.Attribute("IsEnabled")?.Value ?? string.Empty);
        }
    }

    [Fact]
    public void Page_DisablesHorizontalScrollingAndAvoidsFixedMinimumWidths()
    {
        var doc = LoadXaml();
        var scrollViewer = doc.Descendants().Single(e => e.Name.LocalName == "ScrollViewer");

        Assert.Equal("Disabled", scrollViewer.Attribute("HorizontalScrollMode")?.Value);
        Assert.Equal("Disabled", scrollViewer.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.DoesNotContain(doc.Descendants(), e => e.Attribute("MinWidth") is not null);
    }

    [Fact]
    public void PageCode_UsesSharedGateAndFrozenExportFormat()
    {
        var code = LoadPageCode();

        Assert.Contains("BeginOperation(ImportExportOperationKind.Export)", code, StringComparison.Ordinal);
        Assert.Contains("BeginOperation(ImportExportOperationKind.ChooseImportFile)", code, StringComparison.Ordinal);
        Assert.Contains("var format = operation.ExportFormat;", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ExportToText(format)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.SelectedExportFormat", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PageCode_AutoSelectsImportFormatAndDoesNotExposeExceptionDetails()
    {
        var code = LoadPageCode();

        Assert.Contains("ApplySelectedImportFile(operation, file.Name, content)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ex.Message", code, StringComparison.Ordinal);
        Assert.DoesNotContain("MinWidth", code, StringComparison.Ordinal);
    }

    // ── 导航 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void MainWindow_IoCase_NavigatesToImportExportPage()
    {
        // P2-2:io 分支经 INavigationService 下沉导航。校验行为契约不变:
        // 1) io case 调用 _nav.GoToImportExport();
        // 2) FrameNavigationService.GoToImportExport 映射到 typeof(ImportExportPage)。
        var code = LoadMainWindowCode();
        var ioCaseIndex = code.IndexOf("case \"io\":", StringComparison.Ordinal);
        Assert.True(ioCaseIndex >= 0, "case \"io\": not found in MainWindow.xaml.cs");

        var snippet = code.Substring(ioCaseIndex, Math.Min(200, code.Length - ioCaseIndex));
        Assert.Contains("_nav.GoToImportExport()", snippet);

        var navCode = LoadFrameNavServiceCode();
        var goToIndex = navCode.IndexOf("GoToImportExport", StringComparison.Ordinal);
        Assert.True(goToIndex >= 0, "GoToImportExport not found in FrameNavigationService.cs");
        Assert.Contains("typeof(ImportExportPage)", navCode.Substring(goToIndex, Math.Min(120, navCode.Length - goToIndex)));
    }
}
