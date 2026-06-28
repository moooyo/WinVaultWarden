using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class VaultSearchA11yXamlTests
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
    public void SearchBox_HasAccessibleNameAndAutomationId()
    {
        var doc = Load();
        var box = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "AutoSuggestBox"
            && e.Attribute("AutomationProperties.AutomationId")?.Value == "VaultSearchBox");

        Assert.NotNull(box);
        Assert.Equal("搜索保险库", box!.Attribute("AutomationProperties.Name")?.Value);
    }

    [Fact]
    public void SyncButton_HasAccessibleNameAndAutomationId()
    {
        var doc = Load();
        var sync = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Button"
            && e.Attribute("AutomationProperties.AutomationId")?.Value == "VaultSyncButton");

        Assert.NotNull(sync);
        Assert.Equal("同步保险库", sync!.Attribute("AutomationProperties.Name")?.Value);
    }
}
