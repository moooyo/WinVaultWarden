using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class VaultRowActionsXamlTests
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

    [Fact]
    public void RowActions_PanelExists_DefaultTransparent()
    {
        var doc = Load();
        var panel = doc.Descendants().FirstOrDefault(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "RowActions"));
        Assert.NotNull(panel);
        Assert.Equal("0", panel!.Attribute("Opacity")?.Value);
    }

    [Fact]
    public void RowActionButtons_HaveAutomationNames()
    {
        var doc = Load();
        var names = doc.Descendants()
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName == "AutomationProperties.Name")
            .Select(a => a.Value)
            .ToList();
        Assert.Contains("复制", names);
        Assert.Contains("更多", names);
    }
}
