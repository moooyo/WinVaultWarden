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
    public void PackageManifest_NeverShipsOrClaimsBrowserHost()
    {
        // User decision: the browser native-messaging Host is portable-only. The MSIX package
        // must not carry, register, or claim the Host in any way, so its manifest declares
        // neither a fullTrustProcess nor an appExecutionAlias for it.
        var xml = XDocument.Load(FindRepoFile("src", "App", "Package.appxmanifest"));

        Assert.DoesNotContain(
            xml.Descendants(),
            element => (string?)element.Attribute("Category") == "windows.fullTrustProcess");
        Assert.DoesNotContain(
            xml.Descendants(),
            element => (string?)element.Attribute("Category") == "windows.appExecutionAlias");
        Assert.DoesNotContain("BrowserNativeHost", xml.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppProject_ImportsHostPublishLayout()
    {
        var project = File.ReadAllText(FindRepoFile("src", "App", "App.csproj"));

        Assert.Contains("BrowserNativeHost.Publish.targets", project);
    }

    [Fact]
    public void HostPublishLayout_ShipsHostOnlyInPortableBuilds()
    {
        // The Host and its BrowserIntegration payload may only enter unpackaged (portable)
        // builds. Gating on WindowsPackageType == 'None' keeps them out of the MSIX payload.
        var targets = File.ReadAllText(FindRepoFile("src", "App", "BrowserNativeHost.Publish.targets"));

        Assert.Contains("'$(WindowsPackageType)' == 'None'", targets);
        Assert.DoesNotContain("windows.fullTrustProcess", targets);
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
