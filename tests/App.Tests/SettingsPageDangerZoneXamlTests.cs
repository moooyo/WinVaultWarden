using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class SettingsPageDangerZoneXamlTests
{
    private static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "SettingsPage.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("SettingsPage.xaml not found.");
    }

    [Fact]
    public void DeleteAccountButton_IsDisabledUntilImplemented()
    {
        var doc = Load();
        var buttons = doc.Descendants().Where(e => e.Name.LocalName == "Button").ToList();
        var deleteButton = buttons.FirstOrDefault(b =>
            b.Descendants().Any(t => t.Name.LocalName == "TextBlock"
                && (string?)t.Attribute("Text") == "删除账户"));

        Assert.NotNull(deleteButton);
        Assert.Equal("False", (string?)deleteButton!.Attribute("IsEnabled"));
    }
}
