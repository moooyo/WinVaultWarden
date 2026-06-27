using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class VaultPageStateXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void ListColumn_HasEmptyAndNoResultsPanels_BoundToFlags()
    {
        var document = LoadXaml();

        var bindings = document
            .Descendants(Xaml + "StackPanel")
            .Where(p => p.Attribute(X + "Name")?.Value is "VaultEmptyPanel" or "VaultNoResultsPanel")
            .Select(p => (p.Attribute(X + "Name")!.Value, p.Attribute("Visibility")?.Value))
            .ToDictionary(t => t.Item1, t => t.Item2);

        Assert.Contains("VaultEmptyPanel", bindings.Keys);
        Assert.Contains("VaultNoResultsPanel", bindings.Keys);
        Assert.Contains("ViewModel.HasNoItems", bindings["VaultEmptyPanel"]);
        Assert.Contains("ViewModel.NoResults", bindings["VaultNoResultsPanel"]);
        Assert.All(bindings.Values, v => Assert.Contains("BoolToVis", v));
    }

    [Fact]
    public void ListColumn_HasErrorInfoBar_BoundToOperationError()
    {
        var document = LoadXaml();

        var infoBar = document
            .Descendants(Xaml + "InfoBar")
            .Single(b => b.Attribute(X + "Name")?.Value == "VaultErrorInfoBar");

        Assert.Equal("Error", infoBar.Attribute("Severity")?.Value);
        Assert.Contains("ViewModel.OperationError", infoBar.Attribute("Message")?.Value);
        Assert.Contains("ViewModel.HasOperationError", infoBar.Attribute("IsOpen")?.Value);
    }

    private static XDocument LoadXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path))
                return XDocument.Load(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find VaultPage.xaml from the test output directory.");
    }
}
