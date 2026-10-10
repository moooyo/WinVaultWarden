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
    public void ShareLinkAndTextCopyButtons_HaveAccessibleNames()
    {
        var doc = Load();
        var copyButtons = doc.Descendants().Where(e => e.Name.LocalName == "Button"
            && e.Attribute("Click")?.Value is "OnCopyLinkClick" or "OnCopyTextClick" or "OnCopyReceivedTextClick").ToArray();
        Assert.Equal(3, copyButtons.Length);
        Assert.All(copyButtons, button => Assert.False(string.IsNullOrWhiteSpace(button.Attribute("AutomationProperties.Name")?.Value)));
    }
}
