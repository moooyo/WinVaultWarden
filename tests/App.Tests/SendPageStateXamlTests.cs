using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class SendPageStateXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void ErrorInfoBar_BoundToShowError()
    {
        var document = LoadXaml();
        var infoBar = document
            .Descendants(Xaml + "InfoBar")
            .Single(b => b.Attribute(X + "Name")?.Value == "SendErrorInfoBar");

        Assert.Equal("Error", infoBar.Attribute("Severity")?.Value);
        Assert.Contains("ViewModel.Error", infoBar.Attribute("Message")?.Value);
        Assert.Contains("ViewModel.ShowError", infoBar.Attribute("IsOpen")?.Value);
    }

    [Fact]
    public void LoadingRing_BoundToShowLoading()
    {
        var document = LoadXaml();
        var ring = document
            .Descendants(Xaml + "ProgressRing")
            .Single(r => r.Attribute(X + "Name")?.Value == "SendLoadingRing");

        Assert.Contains("ViewModel.ShowLoading", ring.Attribute("IsActive")?.Value);
    }

    [Fact]
    public void EmptyPanel_BoundToShowEmpty()
    {
        var document = LoadXaml();
        var panel = document
            .Descendants(Xaml + "StackPanel")
            .Single(p => p.Attribute(X + "Name")?.Value == "SendEmptyPanel");

        Assert.Contains("ViewModel.ShowEmpty", panel.Attribute("Visibility")?.Value);
    }

    [Fact]
    public void ContentList_BoundToShowContentAndFilteredItems()
    {
        var document = LoadXaml();
        var list = document
            .Descendants(Xaml + "ListView")
            .Single(l => l.Attribute(X + "Name")?.Value == "SendContentList");

        Assert.Contains("ViewModel.ShowContent", list.Attribute("Visibility")?.Value);
        Assert.Contains("ViewModel.FilteredItems", list.Attribute("ItemsSource")?.Value);
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
