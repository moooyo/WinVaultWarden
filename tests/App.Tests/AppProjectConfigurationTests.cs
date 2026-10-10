using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public sealed class AppProjectConfigurationTests
{
    [Fact]
    public void App_UsesExpectedWindowsPlatformPackages()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(
            Path.Combine(root, "src", "App", "App.csproj"));
        var windowsAppSdk = project
            .Descendants("PackageReference")
            .Single(element =>
                (string?)element.Attribute("Include")
                == "Microsoft.WindowsAppSDK");
        var windowsSdkBuildTools = project
            .Descendants("PackageReference")
            .Single(element =>
                (string?)element.Attribute("Include")
                == "Microsoft.Windows.SDK.BuildTools");

        Assert.Equal("2.5.1", (string?)windowsAppSdk.Attribute("Version"));
        Assert.Equal("10.0.26100.4654", (string?)windowsSdkBuildTools.Attribute("Version"));
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
