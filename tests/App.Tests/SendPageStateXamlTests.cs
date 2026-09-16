using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class SendPageStateXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void ErrorInfoBar_BoundToHasError()
    {
        var document = LoadXaml();
        var infoBar = document
            .Descendants(Xaml + "InfoBar")
            .Single(b => b.Attribute(X + "Name")?.Value == "SendErrorInfoBar");

        Assert.Equal("Error", infoBar.Attribute("Severity")?.Value);
        Assert.Contains("ViewModel.Error", infoBar.Attribute("Message")?.Value);
        Assert.Contains("ViewModel.HasError", infoBar.Attribute("IsOpen")?.Value);
        Assert.DoesNotContain("ViewModel.ShowError", infoBar.Attribute("IsOpen")?.Value);
        Assert.Contains("ViewModel.HasError", infoBar.Attribute("Visibility")?.Value);
    }

    [Fact]
    public void ReplacementNoticeInfoBar_IsWarningBoundToNotice()
    {
        var document = LoadXaml();
        var infoBar = document
            .Descendants(Xaml + "InfoBar")
            .Single(b => b.Attribute(X + "Name")?.Value == "SendNoticeInfoBar");

        Assert.Equal("Warning", infoBar.Attribute("Severity")?.Value);
        Assert.Contains("ViewModel.Notice", infoBar.Attribute("Message")?.Value);
        Assert.Contains("ViewModel.ShowNotice", infoBar.Attribute("IsOpen")?.Value);
        Assert.Contains("ViewModel.ShowNotice", infoBar.Attribute("Visibility")?.Value);
    }

    [Fact]
    public void LoadingRing_BoundToShowLoading()
    {
        var document = LoadXaml();
        var ring = document
            .Descendants(Xaml + "ProgressRing")
            .Single(r => r.Attribute(X + "Name")?.Value == "SendLoadingRing");

        Assert.Contains("ViewModel.ShowLoading", ring.Attribute("IsActive")?.Value);
        Assert.Equal("正在加载 Send", ring.Attribute("AutomationProperties.Name")?.Value);
        Assert.Equal("Polite", ring.Attribute("AutomationProperties.LiveSetting")?.Value);
    }

    [Fact]
    public void EmptyPanel_BoundToShowEmpty()
    {
        var document = LoadXaml();
        var panel = document
            .Descendants(Xaml + "StackPanel")
            .Single(p => p.Attribute(X + "Name")?.Value == "SendEmptyPanel");

        Assert.Contains("ViewModel.ShowEmpty", panel.Attribute("Visibility")?.Value);
    }

    [Fact]
    public void ContentList_BoundToShowContentAndFilteredItems()
    {
        var document = LoadXaml();
        var list = document
            .Descendants(Xaml + "ListView")
            .Single(l => l.Attribute(X + "Name")?.Value == "SendContentList");

        Assert.Contains("ViewModel.ShowContent", list.Attribute("Visibility")?.Value);
        Assert.Contains("ViewModel.FilteredItems", list.Attribute("ItemsSource")?.Value);
    }

    [Fact]
    public void DestructiveMenuItems_AreDisabledWhileReconciliationIsPending()
    {
        var document = LoadXaml();
        var actions = document
            .Descendants(Xaml + "MenuFlyoutItem")
            .Where(item => item.Attribute("Text")?.Value is "编辑" or "删除")
            .ToArray();

        Assert.Equal(2, actions.Length);
        Assert.All(actions, item =>
            Assert.Contains("CanModify", item.Attribute("IsEnabled")?.Value));
    }

    [Fact]
    public void RowActions_KeepCopyVisibleAndReachableWithoutHover()
    {
        var document = LoadXaml();
        var actions = document.Descendants(Xaml + "StackPanel")
            .Single(item => item.Attribute(X + "Name")?.Value == "RowActions");
        var copy = actions.Descendants(Xaml + "Button")
            .Single(item => item.Attribute("Click")?.Value == "OnCopyLinkClick");

        Assert.Equal("1", actions.Attribute("Opacity")?.Value);
        Assert.Equal("True", actions.Attribute("IsHitTestVisible")?.Value);
        Assert.Contains(copy.Descendants(Xaml + "TextBlock"), item => item.Attribute("Text")?.Value == "复制链接");
        Assert.Equal("36", copy.Attribute("MinHeight")?.Value);
        Assert.DoesNotContain(document.Descendants(Xaml + "Grid"), item => item.Attribute("PointerExited") is not null);
    }
    [Fact]
    public void ReceivedText_IsSelectableInsideConstrainedScroller()
    {
        var document = LoadXaml();
        var scroller = document
            .Descendants(Xaml + "ScrollViewer")
            .Single(item => item.Attribute(X + "Name")?.Value == "ReceivedTextScrollViewer");
        var text = scroller
            .Descendants(Xaml + "TextBlock")
            .Single(item => item.Attribute(X + "Name")?.Value == "ReceivedTextBlock");
        var layout = document.Root!.Element(Xaml + "Grid")!;
        var contentRow = layout
            .Element(Xaml + "Grid.RowDefinitions")!
            .Elements(Xaml + "RowDefinition")
            .Last();

        Assert.Equal("240", scroller.Attribute("MaxHeight")?.Value);
        Assert.Equal("Auto", scroller.Attribute("VerticalScrollMode")?.Value);
        Assert.Equal("Auto", scroller.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.Contains("ViewModel.HasReceivedText", scroller.Attribute("Visibility")?.Value);
        Assert.Equal("True", text.Attribute("IsTextSelectionEnabled")?.Value);
        Assert.Equal("Wrap", text.Attribute("TextWrapping")?.Value);
        Assert.Contains("ViewModel.ReceivedText", text.Attribute("Text")?.Value);
        Assert.Equal("*", contentRow.Attribute("Height")?.Value);
        Assert.Equal("120", contentRow.Attribute("MinHeight")?.Value);
    }

    [Fact]
    public void ReceivedFileSave_HasBusyControlsAndSingleFlightPageLifetimeWiring()
    {
        var document = LoadXaml();
        var button = document
            .Descendants(Xaml + "Button")
            .Single(item => item.Attribute(X + "Name")?.Value == "SaveReceivedFileButton");
        var progress = document
            .Descendants(Xaml + "ProgressRing")
            .Single(item => item.Attribute(X + "Name")?.Value == "ReceivedFileSaveProgressRing");
        Assert.Equal("OnSaveReceivedFileClick", button.Attribute("Click")?.Value);
        Assert.Equal("False", progress.Attribute("IsActive")?.Value);
        Assert.Equal("正在保存收到的 Send 文件", progress.Attribute("AutomationProperties.Name")?.Value);
        Assert.Equal("Polite", progress.Attribute("AutomationProperties.LiveSetting")?.Value);

        var source = LoadCode();
        var handler = source.IndexOf("OnSaveReceivedFileClick", StringComparison.Ordinal);
        var gate = source.IndexOf("_receivedFileSaveGate.TryEnter", handler, StringComparison.Ordinal);
        var lease = source.IndexOf("AcquireDownloadLease", gate, StringComparison.Ordinal);
        var download = source.IndexOf("DownloadFileAsync", lease, StringComparison.Ordinal);
        Assert.True(handler >= 0 && gate > handler && lease > gate && download > lease);
        Assert.Contains("ReplacePageLifetime();", source, StringComparison.Ordinal);
        Assert.Contains("CancelPageLifetime();", source, StringComparison.Ordinal);
        Assert.Contains("ct: pageOperation.Token", source, StringComparison.Ordinal);
        Assert.Contains("SetReceivedFileSaveBusy(true)", source, StringComparison.Ordinal);
        Assert.Contains("_receivedFileSaveInProgress", source, StringComparison.Ordinal);
    }

    private static XDocument LoadXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "SendPage.xaml");
            if (File.Exists(path))
                return XDocument.Load(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find SendPage.xaml from the test output directory.");
    }

    private static string LoadCode()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "SendPage.xaml.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find SendPage.xaml.cs from the test output directory.");
    }
}
