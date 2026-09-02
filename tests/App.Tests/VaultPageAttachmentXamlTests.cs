using System.Xml.Linq;
using Xunit;

namespace App.Tests;

// Task 5: 附件区 UX —— 校验/进度/禁用/空状态/计数/图标 的 XAML 串断言。
public class VaultPageAttachmentXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AttachmentHeader_BoundToDetailAttachmentHeader()
    {
        var xaml = LoadXamlText();
        Assert.Contains("Text=\"{x:Bind ViewModel.Detail.AttachmentHeader, Mode=OneWay}\"", xaml);
    }

    [Fact]
    public void EmptyState_ShowsWhenNoAttachments_ViaInverseBoolToVis()
    {
        var xaml = LoadXamlText();
        Assert.Contains("暂无附件", xaml);
        Assert.Contains(
            "Visibility=\"{x:Bind ViewModel.Detail.HasAttachments, Mode=OneWay, Converter={StaticResource InverseBoolToVis}}\"",
            xaml);
    }

    [Fact]
    public void AttachmentsItemsControl_VisibleWhenHasAttachments_ViaBoolToVis()
    {
        var document = LoadXaml();
        var itemsControl = document.Descendants(Xaml + "ItemsControl")
            .Single(e => e.Attribute(X + "Name")?.Value == "AttachmentsItems");

        Assert.Equal(
            "{x:Bind ViewModel.Detail.HasAttachments, Mode=OneWay, Converter={StaticResource BoolToVis}}",
            itemsControl.Attribute("Visibility")?.Value);
    }

    [Fact]
    public void DownloadDeleteAddButtons_DisabledWhileVaultBusyOrReadOnly()
    {
        var document = LoadXaml();

        var itemsControl = document.Descendants(Xaml + "ItemsControl")
            .Single(e => e.Attribute(X + "Name")?.Value == "AttachmentsItems");
        var addButton = document.Descendants(Xaml + "Button")
            .Single(b => b.Attribute(X + "Name")?.Value == "AddAttachmentButton");

        foreach (var binding in new[]
                 {
                     itemsControl.Attribute("IsEnabled")?.Value,
                     addButton.Attribute("IsEnabled")?.Value,
                 })
        {
            Assert.Contains("ViewModel.CanModifySelectedItem", binding);
            Assert.Contains("ViewModel.CanStartVaultOperation", binding);
            Assert.Contains("CanBatchOperate", binding);
        }

        var downloadButton = document.Descendants(Xaml + "Button")
            .Single(b => b.Attribute("Click")?.Value == "OnDownloadAttachmentClick");
        var deleteButton = document.Descendants(Xaml + "Button")
            .Single(b => b.Attribute("Click")?.Value == "OnDeleteAttachmentClick");
        Assert.NotNull(downloadButton);
        Assert.NotNull(deleteButton);
    }

    [Fact]
    public void AttachmentRow_TypeIcon_BoundToGlyph()
    {
        var document = LoadXaml();
        var itemsControl = document.Descendants(Xaml + "ItemsControl")
            .Single(e => e.Attribute(X + "Name")?.Value == "AttachmentsItems");

        var typeIcon = itemsControl.Descendants(Xaml + "FontIcon").First();
        Assert.Equal("{x:Bind Glyph}", typeIcon.Attribute("Glyph")?.Value);
    }

    private static string LoadXamlText() => File.ReadAllText(FindXamlPath());

    private static XDocument LoadXaml() => XDocument.Load(FindXamlPath());

    private static string FindXamlPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path))
                return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find VaultPage.xaml from the test output directory.");
    }
}
