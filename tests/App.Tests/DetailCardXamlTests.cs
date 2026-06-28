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
    public void Border_UsesCardBorderStyle_AndCompactPaddingToken()
    {
        var doc = Load();
        var border = doc.Descendants().First(e => e.Name.LocalName == "Border");
        Assert.Equal("{StaticResource CardBorderStyle}", border.Attribute("Style")?.Value);
        Assert.Equal("{StaticResource CardContentPaddingCompact}", border.Attribute("Padding")?.Value);
        Assert.Null(border.Attribute("Background"));
        Assert.Null(border.Attribute("BorderThickness"));
    }
}
