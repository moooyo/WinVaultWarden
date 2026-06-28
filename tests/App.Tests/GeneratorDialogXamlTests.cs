using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class GeneratorDialogXamlTests
{
    internal static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "GeneratorDialog.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("GeneratorDialog.xaml not found.");
    }

    [Fact]
    public void GeneratedValueTextBlocks_UseMonospaceResource_NoInlineConsolas()
    {
        var doc = Load();
        Assert.DoesNotContain(doc.Descendants(),
            e => e.Attribute("FontFamily")?.Value == "Consolas");

        var monoCount = doc.Descendants()
            .Count(e => e.Attribute("FontFamily")?.Value == "{StaticResource MonospaceFontFamily}");
        Assert.Equal(3, monoCount);
    }

    [Fact]
    public void CardBorders_UseCardBorderStyle_AndPaddingTokens()
    {
        var doc = Load();
        var styled = doc.Descendants()
            .Where(e => e.Name.LocalName == "Border"
                && e.Attribute("Style")?.Value == "{StaticResource CardBorderStyle}")
            .ToList();
        Assert.Equal(6, styled.Count);

        // No card Border keeps inline CardBackground brush after refactor.
        Assert.DoesNotContain(doc.Descendants().Where(e => e.Name.LocalName == "Border"),
            e => e.Attribute("Background")?.Value == "{ThemeResource CardBackgroundFillColorDefaultBrush}");

        var paddings = styled.Select(e => e.Attribute("Padding")?.Value).ToList();
        Assert.Equal(3, paddings.Count(p => p == "{StaticResource CardContentPaddingCompact}"));
        Assert.Equal(3, paddings.Count(p => p == "{StaticResource CardContentPadding}"));
    }
}
