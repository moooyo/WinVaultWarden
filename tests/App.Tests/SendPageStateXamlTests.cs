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

        Assert.Equal(4, actions.Length);
        Assert.All(actions, item =>
            Assert.Contains("CanModify", item.Attribute("IsEnabled")?.Value));
    }

    [Fact]
    public void RowActions_ShowHoverCopyAndRetainKeyboardCopy()
    {
        var document = LoadXaml();
        var copy = document.Descendants(Xaml + "Button")
            .Single(item => item.Attribute("Click")?.Value == "OnCopyLinkClick");
        Assert.Equal("复制链接", copy.Attribute("AutomationProperties.Name")?.Value);
        Assert.Contains(document.Descendants(Xaml + "Grid"), item => item.Attribute("PointerEntered")?.Value == "OnRowPointerEntered");
        Assert.Contains(document.Descendants(Xaml + "KeyboardAccelerator"), item =>
            item.Attribute("Key")?.Value == "C" && item.Attribute("Modifiers")?.Value == "Control");
    }
    [Fact]
    public void ReceivedText_IsSelectableInsideReceivedPageScroller()
    {
        var document = LoadXaml();
        var scroller = document
            .Descendants(Xaml + "ScrollViewer")
            .Single(item => item.Attribute(X + "Name")?.Value == "ReceivePane");
        var text = scroller
            .Descendants(Xaml + "TextBlock")
            .Single(item => item.Attribute("Text")?.Value == "{x:Bind ViewModel.DisplayedReceivedText, Mode=OneWay}");
        var layout = document.Root!.Element(Xaml + "Grid")!;
        var contentRow = layout
            .Element(Xaml + "Grid.RowDefinitions")!
            .Elements(Xaml + "RowDefinition")
            .Last();

        Assert.Equal("Disabled", scroller.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.Equal("True", text.Attribute("IsTextSelectionEnabled")?.Value);
        Assert.Equal("Wrap", text.Attribute("TextWrapping")?.Value);
        Assert.Contains("ViewModel.DisplayedReceivedText", text.Attribute("Text")?.Value);
        Assert.Equal("*", contentRow.Attribute("Height")?.Value);
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
