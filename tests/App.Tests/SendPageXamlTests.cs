using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class SendPageXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void SendPage_EditAndDeleteMenuItems_HaveClickHandlers()
    {
        var document = LoadXaml();
        var menuItems = document.Descendants(Xaml + "MenuFlyoutItem").ToList();

        var edit = menuItems.Single(e => e.Attribute("Text")?.Value == "编辑");
        var delete = menuItems.Single(e => e.Attribute("Text")?.Value == "删除");

        Assert.Equal("OnEditSendClick", edit.Attribute("Click")?.Value);
        Assert.Equal("OnDeleteSendClick", delete.Attribute("Click")?.Value);
    }

    private static XDocument LoadXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "SendPage.xaml");
            if (File.Exists(path))
                return XDocument.Load(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find SendPage.xaml from the test output directory.");
    }
}
