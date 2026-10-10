using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class DetailCardXamlTests
{
    private static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Controls", "DetailCard.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("DetailCard.xaml not found.");
    }

    [Fact]
    public void Section_UsesSingleDividerWithoutRaisedCardChrome()
    {
        var doc = Load();
        var border = doc.Descendants().First(e => e.Name.LocalName == "Border");
        Assert.Null(border.Attribute("Style"));
        Assert.Null(border.Attribute("Padding"));
        Assert.Null(border.Attribute("Background"));
        Assert.Null(border.Attribute("CornerRadius"));
        Assert.Equal("0,1,0,0", border.Attribute("BorderThickness")?.Value);
        Assert.Equal("{ThemeResource DividerStrokeColorDefaultBrush}", border.Attribute("BorderBrush")?.Value);
        var title = doc.Descendants().Single(element => element.Name.LocalName == "TextBlock");
        Assert.Equal("{StaticResource CaptionSecondaryTextBlockStyle}", title.Attribute("Style")?.Value);
        Assert.Single(doc.Descendants().Where(element => element.Name.LocalName == "ContentPresenter"));
    }
}
