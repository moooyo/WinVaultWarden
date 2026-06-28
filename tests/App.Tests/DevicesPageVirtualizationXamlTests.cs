using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class DevicesPageVirtualizationXamlTests
{
    private static XDocument LoadDevicesPageXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "DevicesPage.xaml");
            if (File.Exists(path))
                return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("DevicesPage.xaml not found.");
    }

    [Fact]
    public void UsesItemsRepeater_NotBareItemsControl()
    {
        var doc = LoadDevicesPageXaml();

        var repeater = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ItemsRepeater");
        Assert.NotNull(repeater);
        Assert.Equal("{x:Bind ViewModel.Devices}", repeater!.Attribute("ItemsSource")?.Value);

        Assert.DoesNotContain(doc.Descendants(),
            e => e.Name.LocalName == "ItemsControl");

        // 复用同一卡片模板
        Assert.Contains(repeater.Descendants(),
            e => e.Name.LocalName == "DataTemplate");
    }

    [Fact]
    public void Repeater_LivesInsideScrollViewer()
    {
        var doc = LoadDevicesPageXaml();

        var scrollViewer = doc.Descendants()
            .First(e => e.Name.LocalName == "ScrollViewer");
        Assert.Contains(scrollViewer.Descendants(),
            e => e.Name.LocalName == "ItemsRepeater");
    }
}
