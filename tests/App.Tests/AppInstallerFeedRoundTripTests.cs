using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using App.Services;
using Core.Updates;
using Xunit;

namespace App.Tests;

public sealed class AppInstallerFeedRoundTripTests
{
    [Theory]
    [InlineData("x64", "0.2.0")]
    [InlineData("arm64", "0.2.0")]
    [InlineData("x64", "0.2.0+build.7")]
    public void SignedReleaseBuilderOutput_IsAcceptedByInstalledClientPolicy(string architecture, string releaseVersion)
    {
        using var package = new MemoryStream();
        using (var zip = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            XNamespace ns = AppInstallerFeedBuilder.PackageManifestNamespace;
            var manifest = new XElement(ns + "Package", new XElement(ns + "Identity",
                new XAttribute("Name", AppInstallerFeedPolicy.PackageName),
                new XAttribute("Publisher", AppInstallerFeedPolicy.Publisher),
                new XAttribute("ProcessorArchitecture", architecture),
                new XAttribute("Version", "0.2.0.0")));
            using (var writer = new StreamWriter(zip.CreateEntry("AppxManifest.xml").Open(), new UTF8Encoding(false)))
                writer.Write(manifest.ToString());
            using (var writer = new StreamWriter(zip.CreateEntry("AppxSignature.p7x").Open(), new UTF8Encoding(false)))
                writer.Write("Fixture envelope only. Production verification belongs to SignTool and Windows.");
        }
        package.Position = 0;
        var produced = AppInstallerFeedBuilder.Build(package,
            $"WinVaultWarden-{releaseVersion}-{architecture}.msix", $"v{releaseVersion}", releaseVersion, "0.2.0.0",
            new(AppInstallerFeedPolicy.PackageName, AppInstallerFeedPolicy.Publisher), architecture);
        var bytes = Encoding.UTF8.GetBytes(produced.Content);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.True(AppInstallerFeedPolicy.TryValidate(bytes,
            new App.Services.AppInstallerPackageIdentity(AppInstallerFeedPolicy.PackageName,
                AppInstallerFeedPolicy.Publisher, new Version(0, 1, 0, 0), architecture), out var accepted));
        Assert.Equal(produced.FeedUri, accepted!.Source);
        Assert.Equal(produced.PackageUri, accepted.PackageUri);
        Assert.Equal(produced.PackageIdentity.Version, accepted.Package.Version);
    }
}
