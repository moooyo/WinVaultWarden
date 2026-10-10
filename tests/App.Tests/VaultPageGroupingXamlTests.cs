using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class VaultPageGroupingXamlTests
{
    private static XDocument LoadVaultPageXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path))
                return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("VaultPage.xaml not found.");
    }

    [Fact]
    public void List_UsesFlatItemsWithInlineHeadersToAvoidNativeEmptyListRegression()
    {
        var doc = LoadVaultPageXaml();
        // The 2026-10-10 native Debug walkthrough reproduced the empty cipher list
        // previously seen under Native AOT when using grouped CollectionViewSource.
        // Date headers must stay inside flat item templates to avoid that path.
        Assert.DoesNotContain(doc.Descendants(), element => element.Name.LocalName == "CollectionViewSource");
        Assert.DoesNotContain(doc.Descendants(), element => element.Name.LocalName == "GroupStyle");

        var list = doc.Descendants().Single(e =>
            e.Name.LocalName == "ListView" && e.Attribute(Xaml.X + "Name")?.Value == "CipherList");
        Assert.Equal("{x:Bind ViewModel.FilteredItems, Mode=OneWay}", list.Attribute("ItemsSource")?.Value);
        var template = list.Elements().Single(element => element.Name.LocalName == "ListView.ItemTemplate").Elements().Single();
        Assert.Equal("models:CipherListItem", template.Attribute(Xaml.X + "DataType")?.Value);
        var row = template.Descendants().Single(element => element.Attribute(Xaml.X + "Name")?.Value == "RowRoot");
        var header = template.Descendants().Single(element =>
            element.Name.LocalName == "TextBlock" && element.Attribute("Text")?.Value?.Contains("x:Bind GroupHeader") == true);

        Assert.Contains("x:Bind HasGroupHeader", header.Attribute("Visibility")?.Value);
        Assert.Equal("12", header.Attribute("FontSize")?.Value);
        Assert.Equal("52", row.Attribute("Height")?.Value);
        Assert.Same(row.Parent, header.Parent);
        Assert.Contains(header, row.ElementsBeforeSelf());
    }

    [Fact]
    public void InlineHeader_StaysOutsideSelectionChromeWhileNativeSelectionRemainsWired()
    {
        var doc = LoadVaultPageXaml();
        var list = doc.Descendants().Single(element => element.Attribute(Xaml.X + "Name")?.Value == "CipherList");
        var resources = list.Elements().Single(element => element.Name.LocalName == "ListView.Resources");
        var nativeIndicator = resources.Elements().Single(element =>
            element.Attribute(Xaml.X + "Key")?.Value == "ListViewItemSelectionIndicatorVisualEnabled");
        Assert.Equal("False", nativeIndicator.Value);
        foreach (var key in new[] { "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed" })
            Assert.Equal("Transparent", resources.Elements().Single(element => element.Attribute(Xaml.X + "Key")?.Value == key).Attribute("Color")?.Value);

        var row = list.Descendants().Single(element => element.Attribute(Xaml.X + "Name")?.Value == "RowRoot");
        Assert.Contains(row.Descendants(), element => element.Attribute(Xaml.X + "Name")?.Value == "RowSelectionIndicator");
        Assert.Equal("OnRowLoaded", row.Attribute("Loaded")?.Value);
        Assert.Contains("ViewModel.SelectedItem", list.Attribute("SelectedItem")?.Value);
        Assert.Contains("ViewModel.IsSelectionMode", list.Attribute("SelectionMode")?.Value);
        Assert.Equal("OnCipherSelectionChanged", list.Attribute("SelectionChanged")?.Value);
    }
}

internal static class Xaml
{
    public static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
}
