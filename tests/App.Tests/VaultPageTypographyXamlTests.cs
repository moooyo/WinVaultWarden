using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class VaultPageTypographyXamlTests
{
    private static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("VaultPage.xaml not found.");
    }

    private static XElement ByText(XDocument doc, string text) =>
        doc.Descendants().First(e => e.Name.LocalName == "TextBlock"
            && e.Attribute("Text")?.Value == text);

    [Fact]
    public void OptionalSectionsUseFlatRowsAndHideEmptyGroups()
    {
        var doc = Load();
        Assert.Contains(doc.Descendants(), element => element.Name.LocalName == "FieldRow"
            && element.Attribute("Label")?.Value == "备注"
            && element.Attribute("IsMultiline")?.Value == "True"
            && element.Attribute("Value")?.Value?.Contains("ViewModel.Detail.Notes") == true);
        foreach (var property in new[] { "HasAttachments", "HasCustomFields" })
        {
            var section = doc.Descendants().Single(element => element.Name.LocalName == "Border"
                && element.Attribute("Visibility")?.Value?.Contains($"Detail.{property}") == true);
            Assert.Equal("0,1,0,0", section.Attribute("BorderThickness")?.Value);
            Assert.Contains(section.Descendants(), element => element.Name.LocalName == "ItemsControl");
            Assert.DoesNotContain(section.Descendants(), element => element.Name.LocalName == "Expander");
        }
        Assert.DoesNotContain(doc.Descendants(), element => element.Name.LocalName == "Expander"
            && element.Attribute("Header")?.Value == "项目历史");
    }

    [Fact]
    public void RowSubtitleAndHistory_UseCaptionSecondaryStyle()
    {
        var doc = Load();
        var subtitle = ByText(doc, "{Binding Subtitle}");
        Assert.Equal("{StaticResource CaptionSecondaryTextBlockStyle}", subtitle.Attribute("Style")?.Value);
        Assert.Null(subtitle.Attribute("FontSize"));

        var history = doc.Descendants().First(e => e.Name.LocalName == "TextBlock"
            && e.Attribute("Text")?.Value == "{x:Bind ViewModel.Detail.HistoryText, Mode=OneWay}");
        Assert.Equal("{StaticResource CaptionSecondaryTextBlockStyle}", history.Attribute("Style")?.Value);
        Assert.Null(history.Attribute("FontSize"));
        Assert.Null(history.Attribute("Foreground"));
    }
}
