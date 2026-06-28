using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class DevicesPageXamlTests
{
    private static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "DevicesPage.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("DevicesPage.xaml not found.");
    }

    [Fact]
    public void DeviceCard_UsesPaddingToken()
    {
        var doc = Load();
        var card = doc.Descendants().First(e => e.Name.LocalName == "Border"
            && e.Attribute("Background")?.Value == "{ThemeResource CardBackgroundFillColorDefaultBrush}");
        Assert.Equal("{StaticResource CardContentPadding}", card.Attribute("Padding")?.Value);
    }

    [Fact]
    public void LastActive_UsesCaptionSecondaryStyle_NoInlineFontSize()
    {
        var doc = Load();
        var lastActive = doc.Descendants().First(e => e.Name.LocalName == "TextBlock"
            && e.Attribute("Text")?.Value == "{x:Bind LastActive}");
        Assert.Equal("{StaticResource CaptionSecondaryTextBlockStyle}", lastActive.Attribute("Style")?.Value);
        Assert.Null(lastActive.Attribute("FontSize"));
        Assert.Null(lastActive.Attribute("Foreground"));
    }

    [Fact]
    public void NoElevenPxText_Remains()
    {
        var doc = Load();
        Assert.DoesNotContain(doc.Descendants(), e => e.Attribute("FontSize")?.Value == "11");
    }
}
