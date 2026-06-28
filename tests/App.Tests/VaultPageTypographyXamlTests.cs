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
    public void SectionLabels_UseSectionHeaderStyle()
    {
        var doc = Load();
        foreach (var label in new[] { "备注", "项目历史" })
        {
            var tb = ByText(doc, label);
            Assert.Equal("{StaticResource SectionHeaderTextBlockStyle}", tb.Attribute("Style")?.Value);
            Assert.Null(tb.Attribute("Foreground"));
        }
    }

    [Fact]
    public void RowSubtitleAndHistory_UseCaptionSecondaryStyle()
    {
        var doc = Load();
        var subtitle = ByText(doc, "{x:Bind Subtitle}");
        Assert.Equal("{StaticResource CaptionSecondaryTextBlockStyle}", subtitle.Attribute("Style")?.Value);
        Assert.Null(subtitle.Attribute("FontSize"));

        var history = doc.Descendants().First(e => e.Name.LocalName == "TextBlock"
            && e.Attribute("Text")?.Value == "{x:Bind ViewModel.Detail.HistoryText, Mode=OneWay}");
        Assert.Equal("{StaticResource CaptionSecondaryTextBlockStyle}", history.Attribute("Style")?.Value);
        Assert.Null(history.Attribute("FontSize"));
        Assert.Null(history.Attribute("Foreground"));
    }
}
