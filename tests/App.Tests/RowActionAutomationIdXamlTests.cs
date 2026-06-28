using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class RowActionAutomationIdXamlTests
{
    private static XDocument Load(string view)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", view);
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"{view} not found.");
    }

    private static List<string> AutomationIds(XDocument doc) =>
        doc.Descendants()
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName == "AutomationProperties.AutomationId")
            .Select(a => a.Value)
            .ToList();

    [Fact]
    public void VaultRowButtons_HaveAutomationIds()
    {
        var ids = AutomationIds(Load("VaultPage.xaml"));
        Assert.Contains("VaultRowCopyButton", ids);
        Assert.Contains("VaultRowMoreButton", ids);
    }

    [Fact]
    public void SendRowButtons_HaveAutomationIds()
    {
        var ids = AutomationIds(Load("SendPage.xaml"));
        Assert.Contains("SendRowCopyLinkButton", ids);
        Assert.Contains("SendRowMoreButton", ids);
    }
}
