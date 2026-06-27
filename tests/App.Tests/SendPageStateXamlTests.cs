using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class SendPageStateXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void HasErrorInfoBar_BoundToError()
    {
        var document = LoadXaml();
        var infoBar = document
            .Descendants(Xaml + "InfoBar")
            .Single(b => b.Attribute(X + "Name")?.Value == "SendErrorInfoBar");

        Assert.Equal("Error", infoBar.Attribute("Severity")?.Value);
        Assert.Contains("ViewModel.Error", infoBar.Attribute("Message")?.Value);
        Assert.Contains("ViewModel.Error", infoBar.Attribute("IsOpen")?.Value);
    }

    [Fact]
    public void HasProgressRing_BoundToIsBusy()
    {
        var document = LoadXaml();
        var ring = document
            .Descendants(Xaml + "ProgressRing")
            .Single(r => r.Attribute(X + "Name")?.Value == "SendBusyRing");

        Assert.Contains("ViewModel.IsBusy", ring.Attribute("IsActive")?.Value);
    }

    [Fact]
    public void KeepsExistingNoItemsEmptyState()
    {
        var document = LoadXaml();
        var emptyPanel = document
            .Descendants(Xaml + "StackPanel")
            .Where(p => p.Attribute("Visibility")?.Value?.Contains("ViewModel.NoItems") == true);

        Assert.Single(emptyPanel);
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
