using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public sealed class AppProjectConfigurationTests
{
    [Fact]
    public void App_UsesWindowsAppSdk220()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(
            Path.Combine(root, "src", "App", "App.csproj"));
        var package = project
            .Descendants("PackageReference")
            .Single(element =>
                (string?)element.Attribute("Include")
                == "Microsoft.WindowsAppSDK");

        Assert.Equal("2.2.0", (string?)package.Attribute("Version"));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null
            && !File.Exists(Path.Combine(current.FullName, "WinVaultWarden.slnx")))
            current = current.Parent;
        return current?.FullName
            ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
