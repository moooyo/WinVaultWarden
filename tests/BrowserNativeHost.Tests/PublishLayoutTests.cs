using System.Xml.Linq;
using Core.BrowserIntegration;
using Xunit;

namespace BrowserNativeHost.Tests;

public sealed class PublishLayoutTests
{
    [Fact]
    public void Locator_AlwaysUsesAppBaseDirectoryRoot()
    {
        var path = BrowserNativeHostLocator.Resolve(@"C:\Apps\WinVaultWarden");
        Assert.Equal(
            @"C:\Apps\WinVaultWarden\BrowserNativeHost.exe",
            path,
            ignoreCase: true);
    }

    [Fact]
    public void Locator_NormalizesTrailingSeparators()
    {
        var path = BrowserNativeHostLocator.Resolve(@"C:\Apps\WinVaultWarden\");
        Assert.Equal(
            @"C:\Apps\WinVaultWarden\BrowserNativeHost.exe",
            path,
            ignoreCase: true);
    }

    [Fact]
    public void Locator_ExposesFixedHostFileName()
    {
        Assert.Equal("BrowserNativeHost.exe", BrowserNativeHostLocator.FileName);
    }

    [Fact]
    public void PackageManifest_DeclaresRootFullTrustHost()
    {
        var xml = XDocument.Load(FindRepoFile("src", "App", "Package.appxmanifest"));
        var extension = xml.Descendants()
            .Single(element => (string?)element.Attribute("Category") == "windows.fullTrustProcess");

        Assert.Equal("BrowserNativeHost.exe", (string?)extension.Attribute("Executable"));
    }

    [Fact]
    public void AppProject_ImportsHostPublishLayout()
    {
        var project = File.ReadAllText(FindRepoFile("src", "App", "App.csproj"));

        Assert.Contains("BrowserNativeHost.Publish.targets", project);
    }

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
