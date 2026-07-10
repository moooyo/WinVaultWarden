using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class DevicesPageStateXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void HasEmptyPanel_BoundToHasNoDevices()
    {
        var document = LoadXaml();
        var panel = document
            .Descendants(Xaml + "StackPanel")
            .Single(p => p.Attribute(X + "Name")?.Value == "DevicesEmptyPanel");

        Assert.Contains("ViewModel.HasNoDevices", panel.Attribute("Visibility")?.Value);
        Assert.Contains("BoolToVis", panel.Attribute("Visibility")?.Value);
    }

    [Fact]
    public void HasErrorInfoBar_BoundToError()
    {
        var document = LoadXaml();
        var infoBar = document
            .Descendants(Xaml + "InfoBar")
            .Single(b => b.Attribute(X + "Name")?.Value == "DevicesErrorInfoBar");

        Assert.Equal("Error", infoBar.Attribute("Severity")?.Value);
        Assert.Contains("ViewModel.Error", infoBar.Attribute("Message")?.Value);

        var isOpen = infoBar.Attribute("IsOpen")?.Value ?? "";
        Assert.Contains("ViewModel.HasError", isOpen);
        Assert.DoesNotContain("Converter", isOpen);

        var visibility = infoBar.Attribute("Visibility")?.Value ?? "";
        Assert.Contains("ViewModel.HasError", visibility);
    }

    [Fact]
    public void HasProgressRing_BoundToIsBusy()
    {
        var document = LoadXaml();
        var ring = document
            .Descendants(Xaml + "ProgressRing")
            .Single(r => r.Attribute(X + "Name")?.Value == "DevicesBusyRing");

        Assert.Contains("ViewModel.IsBusy", ring.Attribute("IsActive")?.Value);
    }

    // ── Task 6: 请求区域四态 ──────────────────────────────────────────────────

    [Theory]
    [InlineData("DevicesLoadingPanel", "ViewModel.ShowRequestsLoading")]
    [InlineData("DevicesErrorInfoBar", "ViewModel.HasError")]
    [InlineData("RequestsEmptyText", "ViewModel.ShowRequestsEmpty")]
    [InlineData("PendingRequestsRepeater", "ViewModel.ShowRequestsContent")]
    public void RequestStates_AreMutuallyExplicit(string name, string binding)
    {
        var document = LoadXaml();
        var element = document.Descendants()
            .Single(node => node.Attribute(X + "Name")?.Value == name);
        Assert.Contains(binding, element.Attribute("Visibility")?.Value);
    }

    [Fact]
    public void PendingRequestsRepeater_UsesCanOperateForIsEnabled()
    {
        var document = LoadXaml();
        var repeater = document.Descendants()
            .Single(node => node.Attribute(X + "Name")?.Value == "PendingRequestsRepeater");

        Assert.Contains("ViewModel.CanOperate", repeater.Attribute("IsEnabled")?.Value);
    }

    private static XDocument LoadXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "DevicesPage.xaml");
            if (File.Exists(path))
                return XDocument.Load(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find DevicesPage.xaml from the test output directory.");
    }
}
