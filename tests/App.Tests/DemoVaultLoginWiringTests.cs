using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class DemoVaultLoginWiringTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void LoginPage_HasDebugDemoVaultButtonBoundToCommand()
    {
        var document = LoadXaml("LoginPage.xaml");
        var button = RequireByName(document, "DemoVaultButton");

        Assert.Equal("使用演示保险库", button.Attribute("Content")?.Value);
        Assert.Contains("UseDemoVaultCommand", button.Attribute("Command")?.Value);
        Assert.Contains("CanUseDemoVault", button.Attribute("Visibility")?.Value);
        Assert.Equal("Stretch", button.Attribute("HorizontalAlignment")?.Value);
    }

    [Fact]
    public void ServiceConfiguration_RegistersDemoVaultSessionServiceInDebug()
    {
        var source = LoadSource("ServiceConfiguration.cs");

        Assert.Contains("#if DEBUG", source);
        Assert.Contains("AddSingleton<IDemoVaultSessionService, DemoVaultSessionService>()", source);
    }

    private static XElement RequireByName(XDocument document, string name)
    {
        var element = document.Descendants(Xaml + "Button")
            .FirstOrDefault(e => e.Attribute(X + "Name")?.Value == name);
        if (element is null)
        {
            Assert.Fail($"Expected x:Name='{name}' in LoginPage.xaml.");
            throw new InvalidOperationException($"Expected x:Name='{name}' in LoginPage.xaml.");
        }

        return element;
    }

    private static XDocument LoadXaml(string fileName) =>
        XDocument.Load(FindRepoFile("src", "App", "Views", fileName));

    private static string LoadSource(string fileName) =>
        File.ReadAllText(FindRepoFile("src", "App", "Services", fileName));

    private static string FindRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(path))
                return path;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(parts)} from the test output directory.");
    }
}
