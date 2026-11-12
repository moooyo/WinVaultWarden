using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Core.Updates;
using Xunit;

namespace Api.Tests;

public sealed class AppInstallerFeedBuilderTests
{
    private static readonly AppInstallerExpectedIdentity Expected = new("WinVaultWarden", "CN=WinVaultWarden");

    [Theory]
    [InlineData("x64")]
    [InlineData("arm64")]
    public void Build_UsesTheSignedPackageIdentityAndStableArchitectureFeed(string architecture)
    {
        using var package = Package(architecture: architecture);
        var feed = Build(package, architecture: architecture);
        var document = XDocument.Parse(feed.Content);
        XNamespace ns = AppInstallerFeedBuilder.AppInstallerNamespace;
        Assert.Equal(ns + "AppInstaller", document.Root!.Name);
        Assert.Equal("1.0.1.0", (string?)document.Root.Attribute("Version"));
        Assert.Equal($"https://github.com/moooyo/WinVaultWarden/releases/latest/download/WinVaultWarden-{architecture}.appinstaller",
            (string?)document.Root.Attribute("Uri"));
        var main = document.Root.Element(ns + "MainPackage")!;
        Assert.Equal("WinVaultWarden", (string?)main.Attribute("Name"));
        Assert.Equal("CN=WinVaultWarden", (string?)main.Attribute("Publisher"));
        Assert.Equal("0.1.0.0", (string?)main.Attribute("Version"));
        Assert.Equal(architecture, (string?)main.Attribute("ProcessorArchitecture"));
        Assert.Equal($"https://github.com/moooyo/WinVaultWarden/releases/download/v0.1.0/WinVaultWarden-0.1.0-{architecture}.msix",
            (string?)main.Attribute("Uri"));
        Assert.Equal("24", (string?)document.Root.Element(ns + "UpdateSettings")!.Element(ns + "OnLaunch")!.Attribute("HoursBetweenUpdateChecks"));
        Assert.NotNull(document.Root.Element(ns + "UpdateSettings")!.Element(ns + "AutomaticBackgroundTask"));
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "ForceUpdateFromAnyVersion");
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", feed.Content);
    }

    [Fact]
    public void Build_MetadataTagsUseEncodedUrisAndRealCoreVersion()
    {
        using var package = Package(version: "2.3.4.0");
        var feed = AppInstallerFeedBuilder.Build(package, "WinVaultWarden-2.3.4+release.7-x64.msix",
            "v2.3.4+release.7", "2.3.4+release.7", "2.3.4.0", Expected, "x64");
        Assert.Contains("v2.3.4%2Brelease.7", feed.PackageUri.AbsoluteUri);
        Assert.Contains("Version=\"1.2.3.4\"", feed.Content);
    }

    [Theory]
    [InlineData("Other", "CN=WinVaultWarden", "x64", "0.1.0.0")]
    [InlineData("WinVaultWarden", "CN=Other", "x64", "0.1.0.0")]
    [InlineData("WinVaultWarden", "CN=WinVaultWarden", "arm64", "0.1.0.0")]
    [InlineData("WinVaultWarden", "CN=WinVaultWarden", "x64", "0.2.0.0")]
    [InlineData("WinVaultWarden", "CN=WinVaultWarden", "x64", "0.1.0.1")]
    public void Build_RejectsAnyIdentityOrVersionMismatch(string name, string publisher, string architecture, string version)
    {
        using var package = Package(name, publisher, architecture, version);
        Assert.Throws<InvalidDataException>(() => Build(package));
    }

    [Fact]
    public void Build_RejectsAnUnsignedPackageEnvelope()
    {
        using var package = Package(includeSignature: false);
        Assert.Throws<InvalidDataException>(() => Build(package));
    }

    [Fact]
    public void Build_RejectsAnIncorrectAssetName()
    {
        using var package = Package();
        Assert.Throws<InvalidDataException>(() => AppInstallerFeedBuilder.Build(package,
            "WinVaultWarden-0.1.0-x64-unsigned.msix", "v0.1.0", "0.1.0", "0.1.0.0", Expected, "x64"));
    }

    [Fact]
    public void Build_RejectsDuplicateCaseVariantManifests()
    {
        using var package = Package(extraManifestName: "appxmanifest.xml");
        Assert.Throws<InvalidDataException>(() => Build(package));
    }

    [Fact]
    public void Build_RejectsXmlExternalEntities()
    {
        using var package = Package(manifestOverride: """
            <!DOCTYPE Package [<!ENTITY identity SYSTEM "file:///private.txt">]>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="WinVaultWarden" Publisher="&identity;" ProcessorArchitecture="x64" Version="0.1.0.0"/></Package>
            """);
        Assert.Throws<XmlException>(() => Build(package));
    }

    [Fact]
    public void Build_RejectsAnOversizedManifest()
    {
        using var package = Package(manifestOverride: new string(' ', 1024 * 1024 + 1));
        Assert.Throws<InvalidDataException>(() => Build(package));
    }

    [Theory]
    [InlineData("v0.1.0", "0.1.0", "0.1.0.0", true)]
    [InlineData("0.1.0+build.03", "0.1.0+build.03", "0.1.0.0", true)]
    [InlineData("v0.1.0-rc.1", "0.1.0-rc.1", "0.1.0.0", false)]
    [InlineData("v0.1.0-alpha-beta+build", "0.1.0-alpha-beta+build", "0.1.0.0", false)]
    public void StableRelease_RequiresValidMatchingMetadata(string tag, string version, string manifestVersion, bool expected) =>
        Assert.Equal(expected, AppInstallerFeedBuilder.IsStableRelease(tag, version, manifestVersion));

    [Theory]
    [InlineData("v0.1.0", "0.1.1", "0.1.1.0")]
    [InlineData("v0.1.0", "0.1.0", "0.1.0.1")]
    [InlineData("v00.1.0", "00.1.0", "0.1.0.0")]
    [InlineData("v0.1.0-01", "0.1.0-01", "0.1.0.0")]
    [InlineData("v0.1.0+", "0.1.0+", "0.1.0.0")]
    [InlineData("v0.1.0+bad/key", "0.1.0+bad/key", "0.1.0.0")]
    [InlineData("v65536.1.0", "65536.1.0", "65536.1.0.0")]
    public void StableRelease_RejectsUnsafeOrInconsistentInputs(string tag, string version, string manifestVersion) =>
        Assert.Throws<InvalidDataException>(() => AppInstallerFeedBuilder.IsStableRelease(tag, version, manifestVersion));

    [Fact]
    public void Build_NeverGeneratesAStableFeedForAPrerelease()
    {
        using var package = Package();
        Assert.Throws<InvalidOperationException>(() => AppInstallerFeedBuilder.Build(package,
            "WinVaultWarden-0.1.0-rc.1-x64.msix", "v0.1.0-rc.1", "0.1.0-rc.1", "0.1.0.0", Expected, "x64"));
    }

    [Fact]
    public void RepositoryIdentity_IsReadFromTheManifestRatherThanDuplicatedConstants()
    {
        using var manifest = new MemoryStream(Encoding.UTF8.GetBytes("""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="ActualName" Publisher="CN=Actual Publisher &amp; Company" Version="1.0.0.0"/></Package>
            """));
        Assert.Equal(new AppInstallerExpectedIdentity("ActualName", "CN=Actual Publisher & Company"),
            AppInstallerFeedBuilder.ReadExpectedIdentity(manifest));
    }

    private static AppInstallerFeed Build(Stream package, string architecture = "x64") =>
        AppInstallerFeedBuilder.Build(package, $"WinVaultWarden-0.1.0-{architecture}.msix",
            "v0.1.0", "0.1.0", "0.1.0.0", Expected, architecture);

    private static MemoryStream Package(
        string name = "WinVaultWarden",
        string publisher = "CN=WinVaultWarden",
        string architecture = "x64",
        string version = "0.1.0.0",
        bool includeSignature = true,
        string? extraManifestName = null,
        string? manifestOverride = null)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = manifestOverride ?? new XElement(
                XName.Get("Package", AppInstallerFeedBuilder.PackageManifestNamespace),
                new XElement(XName.Get("Identity", AppInstallerFeedBuilder.PackageManifestNamespace),
                    new XAttribute("Name", name), new XAttribute("Publisher", publisher),
                    new XAttribute("ProcessorArchitecture", architecture), new XAttribute("Version", version))).ToString();
            WriteEntry("AppxManifest.xml", manifest);
            if (extraManifestName is not null)
                WriteEntry(extraManifestName, manifest);
            if (includeSignature)
                WriteEntry("AppxSignature.p7x", "Fixture signature envelope; cryptographic verification belongs to SignTool.");

            void WriteEntry(string entryName, string content)
            {
                using var writer = new StreamWriter(archive.CreateEntry(entryName).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        stream.Position = 0;
        return stream;
    }
}
