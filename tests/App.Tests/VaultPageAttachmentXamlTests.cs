using System.Xml.Linq;
using Xunit;

namespace App.Tests;

// Attachment rows retain metadata, guarded actions, and the v3 flat section layout.
public class VaultPageAttachmentXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AttachmentSection_UsesFlatDividerWithoutAnExpanderHeader()
    {
        var document = LoadXaml();
        var items = document.Descendants(Xaml + "ItemsControl")
            .Single(element => element.Attribute(X + "Name")?.Value == "AttachmentsItems");
        var section = items.Ancestors(Xaml + "Border").First();

        Assert.Equal("0,1,0,0", section.Attribute("BorderThickness")?.Value);
        Assert.DoesNotContain(items.Ancestors(), element => element.Name == Xaml + "Expander");
        Assert.Contains(items.Descendants(Xaml + "TextBlock"), element => element.Attribute("Text")?.Value == "附件");
    }

    [Fact]
    public void EmptyAttachmentGroup_IsHidden()
    {
        var document = LoadXaml();
        var items = document.Descendants(Xaml + "ItemsControl")
            .Single(element => element.Attribute(X + "Name")?.Value == "AttachmentsItems");
        var section = items.Ancestors(Xaml + "Border").First();

        Assert.Equal(
            "{x:Bind ViewModel.Detail.HasAttachments, Mode=OneWay, Converter={StaticResource BoolToVis}}",
            section.Attribute("Visibility")?.Value);
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
    public void AttachmentRow_ShowsFileNameAndSizeBesideDownloadAction()
    {
        var document = LoadXaml();
        var itemsControl = document.Descendants(Xaml + "ItemsControl")
            .Single(e => e.Attribute(X + "Name")?.Value == "AttachmentsItems");

        Assert.Contains(itemsControl.Descendants(Xaml + "TextBlock"),
            element => element.Attribute("Text")?.Value == "{Binding FileName}");
        Assert.Contains(itemsControl.Descendants(Xaml + "TextBlock"),
            element => element.Attribute("Text")?.Value == "{Binding SizeName}");
        var download = itemsControl.Descendants(Xaml + "Button")
            .Single(element => element.Attribute("Click")?.Value == "OnDownloadAttachmentClick");
        Assert.Equal("下载附件", download.Attribute("AutomationProperties.Name")?.Value);
        Assert.Equal("\uE896", download.Descendants(Xaml + "FontIcon").Single().Attribute("Glyph")?.Value);
    }

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
