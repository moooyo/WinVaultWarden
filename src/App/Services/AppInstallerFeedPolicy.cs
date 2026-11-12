using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace App.Services;

public sealed record AppInstallerPackageIdentity(
    string Name,
    string Publisher,
    Version Version,
    string Architecture);

public sealed record ValidatedAppInstallerFeed(
    Uri Source,
    AppInstallerPackageIdentity Package,
    Uri PackageUri);

/// <summary>
/// Restricts enrollment to this repository's stable feed and the installed package identity.
/// Windows App Installer still verifies the actual MSIX signature and deployment eligibility.
/// </summary>
public static class AppInstallerFeedPolicy
{
    public const int MaximumFeedBytes = 64 * 1024;
    public const string PackageName = "WinVaultWarden";
    public const string Publisher = "CN=WinVaultWarden";
    public const string Schema = "http://schemas.microsoft.com/appx/appinstaller/2018";

    public static bool TryGetFeedUri(string architecture, out Uri? uri)
    {
        uri = architecture switch
        {
            "x64" => new("https://github.com/moooyo/WinVaultWarden/releases/latest/download/WinVaultWarden-x64.appinstaller"),
            "arm64" => new("https://github.com/moooyo/WinVaultWarden/releases/latest/download/WinVaultWarden-arm64.appinstaller"),
            _ => null,
        };
        return uri is not null;
    }

    public static bool IsExpectedInstalledIdentity(AppInstallerPackageIdentity identity) =>
        identity.Name == PackageName && identity.Publisher == Publisher
        && identity.Version.Revision >= 0
        && TryGetFeedUri(identity.Architecture, out _);

    public static bool TryValidate(
        ReadOnlySpan<byte> content,
        AppInstallerPackageIdentity installed,
        out ValidatedAppInstallerFeed? feed)
    {
        feed = null;
        if (content.Length == 0 || content.Length > MaximumFeedBytes
            || !IsExpectedInstalledIdentity(installed)
            || !TryGetFeedUri(installed.Architecture, out var expectedSource))
        {
            return false;
        }

        try
        {
            // The producer writes ASCII-compatible UTF-8. Reject alternate encodings and
            // arbitrary entity resolution before handing any metadata to Windows.
            var xml = new UTF8Encoding(false, true).GetString(content);
            if (xml.Any(character => character > 127))
                return false;
            using var input = new StringReader(xml);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumFeedBytes,
                IgnoreComments = true,
            });
            var document = XDocument.Load(reader);
            XNamespace ns = Schema;
            var root = document.Root;
            if (root is null || root.Name != ns + "AppInstaller"
                || !HasOnlyAttributes(root, "Version", "Uri")
                || (string?)root.Attribute("Uri") != expectedSource!.AbsoluteUri
                || root.Elements().Count() != 2)
            {
                return false;
            }

            var packages = root.Elements(ns + "MainPackage").ToArray();
            var settings = root.Elements(ns + "UpdateSettings").ToArray();
            if (packages.Length != 1 || settings.Length != 1)
                return false;
            var package = packages[0];
            if (!HasOnlyAttributes(package, "Name", "Publisher", "Version", "ProcessorArchitecture", "Uri")
                || package.HasElements
                || (string?)package.Attribute("Name") != installed.Name
                || (string?)package.Attribute("Publisher") != installed.Publisher
                || (string?)package.Attribute("ProcessorArchitecture") != installed.Architecture
                || !TryQuadVersion((string?)package.Attribute("Version"), out var packageVersion)
                || packageVersion.Revision != 0
                || packageVersion < installed.Version
                || !TryQuadVersion((string?)root.Attribute("Version"), out var feedVersion)
                || feedVersion != new Version(1, packageVersion.Major, packageVersion.Minor, packageVersion.Build)
                || !TryPackageUri((string?)package.Attribute("Uri"), packageVersion, installed.Architecture, out var packageUri))
            {
                return false;
            }

            var update = settings[0];
            var launch = update.Element(ns + "OnLaunch");
            var background = update.Element(ns + "AutomaticBackgroundTask");
            if (update.HasAttributes || update.Elements().Count() != 2
                || launch is null || background is null
                || !HasOnlyAttributes(launch, "HoursBetweenUpdateChecks")
                || (string?)launch.Attribute("HoursBetweenUpdateChecks") != "24"
                || launch.HasElements || background.HasElements || background.HasAttributes)
            {
                return false;
            }

            feed = new(expectedSource, installed with { Version = packageVersion }, packageUri!);
            return true;
        }
        catch (Exception exception) when (exception is XmlException or DecoderFallbackException
            or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public static bool IsAllowedDownloadLocation(Uri uri, string architecture)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsoluteUri.Length > 16384)
        {
            return false;
        }
        if (uri.IdnHost.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal);
        if (!uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Query.Length != 0)
            return false;
        if (TryGetFeedUri(architecture, out var stable) && uri == stable)
            return true;
        const string prefix = "/moooyo/WinVaultWarden/releases/download/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var parts = uri.AbsolutePath[prefix.Length..].Split('/');
        return parts.Length == 2
            && IsStableRelease(Uri.UnescapeDataString(parts[0]), out _)
            && Uri.UnescapeDataString(parts[1]) == $"WinVaultWarden-{architecture}.appinstaller";
    }

    private static bool TryPackageUri(string? value, Version version, string architecture, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps || !candidate.IsDefaultPort
            || !candidate.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || candidate.UserInfo.Length != 0 || candidate.Query.Length != 0 || candidate.Fragment.Length != 0)
        {
            return false;
        }
        const string prefix = "/moooyo/WinVaultWarden/releases/download/";
        if (!candidate.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var parts = candidate.AbsolutePath[prefix.Length..].Split('/');
        if (parts.Length != 2
            || !IsStableRelease(Uri.UnescapeDataString(parts[0]), out var release)
            || release.Major != version.Major || release.Minor != version.Minor || release.Patch != version.Build
            || Uri.UnescapeDataString(parts[1]) != $"WinVaultWarden-{release.Text}-{architecture}.msix")
        {
            return false;
        }
        uri = candidate;
        return true;
    }

    private static bool IsStableRelease(string value, out ReleaseVersion version) =>
        ReleaseVersion.TryParse(value, allowPackageRevision: false, out version) && !version.IsPrerelease;

    private static bool TryQuadVersion(string? value, out Version version)
    {
        version = null!;
        if (value is null || value.Length > 23 || !Version.TryParse(value, out var parsed)
            || parsed.Revision < 0 || parsed.Major > ushort.MaxValue || parsed.Minor > ushort.MaxValue
            || parsed.Build > ushort.MaxValue || parsed.Revision > ushort.MaxValue
            || parsed.ToString() != value)
        {
            return false;
        }
        version = parsed;
        return true;
    }

    private static bool HasOnlyAttributes(XElement element, params string[] names) =>
        element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)
            .All(attribute => attribute.Name.Namespace == XNamespace.None
                && names.Contains(attribute.Name.LocalName, StringComparer.Ordinal));
}
