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
}
