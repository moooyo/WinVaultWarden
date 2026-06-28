using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class FieldRowXamlTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Controls", "FieldRow.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("FieldRow.xaml not found.");
    }

    [Fact]
    public void RevealButton_HasInitialShowAccessibleName()
    {
        var doc = Load();
        var reveal = doc.Descendants().FirstOrDefault(e =>
            e.Attribute(X + "Name")?.Value == "RevealButton");

        Assert.NotNull(reveal);
        Assert.Equal("显示", reveal!.Attribute("AutomationProperties.Name")?.Value);
        Assert.Equal("显示", reveal.Attribute("ToolTipService.ToolTip")?.Value);
    }
}
