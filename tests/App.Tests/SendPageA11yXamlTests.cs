using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class SendPageA11yXamlTests
{
    private static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "SendPage.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("SendPage.xaml not found.");
    }

    [Fact]
    public void DeleteDateCell_BindsAccessibleLabel()
    {
        var doc = Load();
        var dateCell = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "TextBlock"
            && e.Attribute("Text")?.Value == "{x:Bind DeleteDate}");

        Assert.NotNull(dateCell);
        Assert.Equal(
            "{x:Bind DeleteDateAccessibleLabel}",
            dateCell!.Attribute("AutomationProperties.Name")?.Value);
    }
}
