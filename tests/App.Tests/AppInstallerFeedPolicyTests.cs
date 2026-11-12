using System.Text;
using App.Services;
using Xunit;

namespace App.Tests;

public sealed class AppInstallerFeedPolicyTests
{
    private static readonly AppInstallerPackageIdentity Installed = new(
        "WinVaultWarden", "CN=WinVaultWarden", new Version(0, 1, 0, 0), "x64");

    [Theory]
    [InlineData("x64")]
    [InlineData("arm64")]
    public void OfficialFeed_RequiresMatchingIdentityArchitectureAndNondecreasingVersion(string architecture)
    {
        var installed = Installed with { Architecture = architecture };
        Assert.True(AppInstallerFeedPolicy.TryValidate(Bytes(Xml(architecture: architecture)), installed, out var feed));
        Assert.Equal(new Version(0, 2, 0, 0), feed!.Package.Version);
        Assert.Equal(architecture, feed.Package.Architecture);
        Assert.Equal($"https://github.com/moooyo/WinVaultWarden/releases/latest/download/WinVaultWarden-{architecture}.appinstaller",
            feed.Source.AbsoluteUri);
    }

    [Fact]
    public void CurrentVersion_CanEnrollWithoutReinstallingANewerVersion() =>
        Assert.True(AppInstallerFeedPolicy.TryValidate(
            Bytes(Xml(packageVersion: "0.1.0.0", feedVersion: "1.0.1.0", tag: "v0.1.0", fileVersion: "0.1.0")),
            Installed, out _));

    [Theory]
    [InlineData("OtherApp", "CN=WinVaultWarden", "x64")]
    [InlineData("WinVaultWarden", "CN=AnotherPublisher", "x64")]
    [InlineData("WinVaultWarden", "CN=WinVaultWarden", "arm64")]
    public void PackageIdentityMismatch_IsRejected(string name, string publisher, string architecture) =>
        Assert.False(AppInstallerFeedPolicy.TryValidate(
            Bytes(Xml(name: name, publisher: publisher, architecture: architecture)), Installed, out _));

    [Theory]
    [InlineData("1.2.0.0")]
    [InlineData("01.0.2.0")]
    [InlineData("1.0.2")]
    public void InvalidOrUnrelatedFeedVersion_IsRejected(string version) =>
        Assert.False(AppInstallerFeedPolicy.TryValidate(Bytes(Xml(feedVersion: version)), Installed, out _));

    [Fact]
    public void PackageDowngrade_IsRejected() =>
        Assert.False(AppInstallerFeedPolicy.TryValidate(Bytes(Xml()),
            Installed with { Version = new Version(0, 3, 0, 0) }, out _));

    [Theory]
    [InlineData("https://evil.example/WinVaultWarden-x64.appinstaller")]
    [InlineData("https://github.com/other/WinVaultWarden/releases/latest/download/WinVaultWarden-x64.appinstaller")]
    [InlineData("https://github.com/moooyo/WinVaultWarden/releases/download/v0.2.0/WinVaultWarden-x64.appinstaller")]
    public void FeedSourceMustRemainTheFixedStableChannel(string source) =>
        Assert.False(AppInstallerFeedPolicy.TryValidate(Bytes(Xml(source: source)), Installed, out _));

    [Theory]
    [InlineData("https://evil.example/WinVaultWarden-0.2.0-x64.msix")]
    [InlineData("https://github.com/other/repo/releases/download/v0.2.0/WinVaultWarden-0.2.0-x64.msix")]
    [InlineData("https://github.com/moooyo/WinVaultWarden/releases/download/v0.1.0/WinVaultWarden-0.2.0-x64.msix")]
    [InlineData("https://github.com/moooyo/WinVaultWarden/releases/download/v0.2.0/WinVaultWarden-0.2.0-arm64.msix")]
    [InlineData("https://github.com/moooyo/WinVaultWarden/releases/download/v0.2.0/WinVaultWarden-0.2.0-x64.msix?other=true")]
    public void PackageUriMustMatchOfficialReleaseVersionAndArchitecture(string packageUri) =>
        Assert.False(AppInstallerFeedPolicy.TryValidate(Bytes(Xml(packageUri: packageUri)), Installed, out _));

    [Fact]
    public void PrereleaseCannotBeEnrolledInTheStableChannel() =>
        Assert.False(AppInstallerFeedPolicy.TryValidate(
            Bytes(Xml(tag: "v0.2.0-preview.1", fileVersion: "0.2.0-preview.1")), Installed, out _));

    [Fact]
    public void BuildMetadataDoesNotChangeThePackageIdentity() =>
        Assert.True(AppInstallerFeedPolicy.TryValidate(
            Bytes(Xml(tag: "v0.2.0+build.1", fileVersion: "0.2.0+build.1")), Installed, out _));

    [Theory]
    [InlineData("<ForceUpdateFromAnyVersion>true</ForceUpdateFromAnyVersion>")]
    [InlineData("<OptionalPackages />")]
    [InlineData("<MainPackage Name=\"OtherApp\" />")]
    public void UnreviewedInstallerElements_AreRejected(string extra) =>
        Assert.False(AppInstallerFeedPolicy.TryValidate(
            Bytes(Xml().Replace("</AppInstaller>", extra + "</AppInstaller>", StringComparison.Ordinal)), Installed, out _));

    [Fact]
    public void EntityResolution_IsDisabled()
    {
        var content = "<!DOCTYPE AppInstaller [<!ENTITY x SYSTEM 'file:///etc/passwd'>]>" + Xml();
        Assert.False(AppInstallerFeedPolicy.TryValidate(Bytes(content), Installed, out _));
    }

    [Fact]
    public void OversizedFeed_IsRejectedBeforeParsing() =>
        Assert.False(AppInstallerFeedPolicy.TryValidate(new byte[AppInstallerFeedPolicy.MaximumFeedBytes + 1], Installed, out _));

    [Theory]
    [InlineData("https://github.com/moooyo/WinVaultWarden/releases/latest/download/WinVaultWarden-x64.appinstaller", true)]
    [InlineData("https://github.com/moooyo/WinVaultWarden/releases/download/v0.2.0/WinVaultWarden-x64.appinstaller", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/123/asset?token=public", true)]
    [InlineData("http://github.com/moooyo/WinVaultWarden/releases/latest/download/WinVaultWarden-x64.appinstaller", false)]
    [InlineData("https://github.com/other/repo/releases/download/v0.2.0/WinVaultWarden-x64.appinstaller", false)]
    [InlineData("https://release-assets.githubusercontent.com/other/asset", false)]
    [InlineData("https://release-assets.githubusercontent.com.evil.example/github-production-release-asset/asset", false)]
    [InlineData("https://github.com/moooyo/WinVaultWarden/releases/download/v0.2.0-preview.1/WinVaultWarden-x64.appinstaller", false)]
    [InlineData("https://github.com/moooyo/WinVaultWarden/releases/download/v0.2.0/WinVaultWarden-arm64.appinstaller", false)]
    public void Redirects_AreLimitedToTheOfficialFeedAndAssetTransport(string value, bool expected) =>
        Assert.Equal(expected, AppInstallerFeedPolicy.IsAllowedDownloadLocation(new Uri(value), "x64"));

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Xml(
        string architecture = "x64",
        string name = "WinVaultWarden",
        string publisher = "CN=WinVaultWarden",
        string packageVersion = "0.2.0.0",
        string feedVersion = "1.0.2.0",
        string tag = "v0.2.0",
        string fileVersion = "0.2.0",
        string? source = null,
        string? packageUri = null) => $$"""
        <AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2018"
            Version="{{feedVersion}}"
            Uri="{{source ?? $"https://github.com/moooyo/WinVaultWarden/releases/latest/download/WinVaultWarden-{architecture}.appinstaller"}}">
            <MainPackage Name="{{name}}" Publisher="{{publisher}}" Version="{{packageVersion}}"
                ProcessorArchitecture="{{architecture}}"
                Uri="{{packageUri ?? $"https://github.com/moooyo/WinVaultWarden/releases/download/{Uri.EscapeDataString(tag)}/WinVaultWarden-{Uri.EscapeDataString(fileVersion)}-{architecture}.msix"}}" />
            <UpdateSettings><OnLaunch HoursBetweenUpdateChecks="24"/><AutomaticBackgroundTask/></UpdateSettings>
        </AppInstaller>
        """;
}
