using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class VaultPageAttachmentsXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void VaultPage_HasAttachmentsItemsControl_BoundToDetailAttachments()
    {
        var document = LoadXaml();
        var itemsControl = document.Descendants(Xaml + "ItemsControl")
            .Single(e => e.Attribute(X + "Name")?.Value == "AttachmentsItems");

        Assert.Contains("ViewModel.Detail.Attachments", itemsControl.Attribute("ItemsSource")?.Value ?? "");
    }

    [Fact]
    public void VaultPage_AttachmentRow_BindsFileNameAndSizeName()
    {
        var document = LoadXaml();
        var itemsControl = document.Descendants(Xaml + "ItemsControl")
            .Single(e => e.Attribute(X + "Name")?.Value == "AttachmentsItems");

        var textBlocks = itemsControl.Descendants(Xaml + "TextBlock").ToList();
        Assert.Contains(textBlocks, t => (t.Attribute("Text")?.Value ?? "").Contains("FileName"));
        Assert.Contains(textBlocks, t => (t.Attribute("Text")?.Value ?? "").Contains("SizeName"));
    }

    [Fact]
    public void VaultPage_AttachmentRow_HasDownloadAndDeleteHandlers()
    {
        var document = LoadXaml();
        var itemsControl = document.Descendants(Xaml + "ItemsControl")
            .Single(e => e.Attribute(X + "Name")?.Value == "AttachmentsItems");

        var buttons = itemsControl.Descendants(Xaml + "Button").ToList();
        Assert.Contains(buttons, b => b.Attribute("Click")?.Value == "OnDownloadAttachmentClick");
        Assert.Contains(buttons, b => b.Attribute("Click")?.Value == "OnDeleteAttachmentClick");
    }

    [Fact]
    public void VaultPage_HasAddAttachmentButton_WithClickHandler()
    {
        var document = LoadXaml();
        var addButton = document.Descendants(Xaml + "Button")
            .Single(b => b.Attribute(X + "Name")?.Value == "AddAttachmentButton");

        Assert.Equal("OnAddAttachmentClick", addButton.Attribute("Click")?.Value);
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
