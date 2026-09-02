using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class VaultPageStateXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void ListColumn_HasEmptyAndNoResultsPanels_BoundToFlags()
    {
        var document = LoadXaml();

        var bindings = document
            .Descendants(Xaml + "StackPanel")
            .Where(p => p.Attribute(X + "Name")?.Value is "VaultEmptyPanel" or "VaultNoResultsPanel")
            .Select(p => (p.Attribute(X + "Name")!.Value, p.Attribute("Visibility")?.Value))
            .ToDictionary(t => t.Item1, t => t.Item2);

        Assert.Contains("VaultEmptyPanel", bindings.Keys);
        Assert.Contains("VaultNoResultsPanel", bindings.Keys);
        Assert.Contains("ViewModel.HasNoItems", bindings["VaultEmptyPanel"]);
        Assert.Contains("ViewModel.NoResults", bindings["VaultNoResultsPanel"]);
        Assert.All(bindings.Values, v => Assert.Contains("BoolToVis", v));
    }

    [Fact]
    public void ListColumn_HasErrorInfoBar_BoundToOperationError()
    {
        var document = LoadXaml();

        var infoBar = document
            .Descendants(Xaml + "InfoBar")
            .Single(b => b.Attribute(X + "Name")?.Value == "VaultErrorInfoBar");

        Assert.Equal("Error", infoBar.Attribute("Severity")?.Value);
        Assert.Contains("ViewModel.OperationError", infoBar.Attribute("Message")?.Value);
        Assert.Contains("ViewModel.HasOperationError", infoBar.Attribute("IsOpen")?.Value);
    }

    [Fact]
    public void ListColumn_HasSkippedCipherInfoBar_BoundToSkippedFlags()
    {
        var document = LoadXaml();

        var infoBar = document
            .Descendants(Xaml + "InfoBar")
            .Single(b => b.Attribute(X + "Name")?.Value == "VaultSkippedInfoBar");

        Assert.Equal("Informational", infoBar.Attribute("Severity")?.Value);
        Assert.Equal("True", infoBar.Attribute("IsClosable")?.Value);
        Assert.Contains("ViewModel.SkippedCipherMessage", infoBar.Attribute("Message")?.Value);
        Assert.Contains("ViewModel.HasSkippedCiphers", infoBar.Attribute("IsOpen")?.Value);
    }

    [Fact]
    public void ListColumn_HasOrganizationUnavailableInfoBar_BoundToDedicatedState()
    {
        var document = LoadXaml();
        var infoBar = document
            .Descendants(Xaml + "InfoBar")
            .Single(b => b.Attribute(X + "Name")?.Value == "VaultOrganizationUnavailableInfoBar");

        Assert.Contains(
            "ViewModel.UnsupportedOrganizationCipherMessage",
            infoBar.Attribute("Message")?.Value);
        Assert.Contains(
            "ViewModel.HasUnsupportedOrganizationCiphers",
            infoBar.Attribute("IsOpen")?.Value);
    }

    [Fact]
    public void ListColumn_HasOfflineInfoBar_BoundToGenericStaleState()
    {
        var document = LoadXaml();

        var infoBar = document
            .Descendants(Xaml + "InfoBar")
            .Single(b => b.Attribute(X + "Name")?.Value == "VaultOfflineInfoBar");

        Assert.Equal("Warning", infoBar.Attribute("Severity")?.Value);
        Assert.Equal("False", infoBar.Attribute("IsClosable")?.Value);
        Assert.Contains("ViewModel.ShowDataStaleInfo", infoBar.Attribute("IsOpen")?.Value);
    }

    [Fact]
    public void ListColumn_HasCommittedWriteWarning_SeparateFromErrorInfoBar()
    {
        var document = LoadXaml();

        var infoBar = document
            .Descendants(Xaml + "InfoBar")
            .Single(b => b.Attribute(X + "Name")?.Value == "VaultCommittedWarningInfoBar");

        Assert.Equal("Warning", infoBar.Attribute("Severity")?.Value);
        Assert.Equal("False", infoBar.Attribute("IsClosable")?.Value);
        Assert.Contains("ViewModel.OperationWarning", infoBar.Attribute("Message")?.Value);
        Assert.Contains("ViewModel.ShowOperationWarningInfo", infoBar.Attribute("IsOpen")?.Value);
        Assert.DoesNotContain("OperationError", infoBar.Attribute("Message")?.Value);
    }

    [Fact]
    public void DetailColumn_NoSelectionPanel_IsCenteredAndBoundToPlaceholderFlag()
    {
        var document = LoadXaml();

        var panel = document
            .Descendants(Xaml + "StackPanel")
            .Single(p => p.Attribute(X + "Name")?.Value == "VaultNoSelectionPanel");

        // 居中修复:右栏空状态须在整个右栏内水平+垂直居中(取代旧的靠左 + 固定上边距)。
        Assert.Equal("Center", panel.Attribute("VerticalAlignment")?.Value);
        Assert.Equal("Center", panel.Attribute("HorizontalAlignment")?.Value);
        // 可见性绑定到 ShowNoSelectionPlaceholder(排除“新建条目”编辑态)。
        Assert.Contains("ViewModel.ShowNoSelectionPlaceholder", panel.Attribute("Visibility")?.Value);
        Assert.Contains("BoolToVis", panel.Attribute("Visibility")?.Value);
    }

    [Fact]
    public void BusyPanel_HasPoliteLiveRegionText()
    {
        var document = LoadXaml();

        var busyPanel = document
            .Descendants(Xaml + "StackPanel")
            .Single(p => p.Attribute(X + "Name")?.Value == "VaultBusyPanel");

        var hasPoliteText = busyPanel
            .Descendants(Xaml + "TextBlock")
            .Any(t => t.Attribute("AutomationProperties.LiveSetting")?.Value == "Polite");

        Assert.True(hasPoliteText,
            "VaultBusyPanel must contain a TextBlock with AutomationProperties.LiveSetting=\"Polite\".");
    }

    private static XDocument LoadXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path))
                return XDocument.Load(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find VaultPage.xaml from the test output directory.");
    }
}
