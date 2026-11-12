using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Core.Updates;

public sealed record AppInstallerExpectedIdentity(string Name, string Publisher);

public sealed record AppInstallerPackageIdentity(
    string Name, string Publisher, string Architecture, Version Version);

public sealed record AppInstallerFeed(
    string FileName,
    string Content,
    Uri FeedUri,
    Uri PackageUri,
    AppInstallerPackageIdentity PackageIdentity);

/// <summary>
/// Creates stable MSIX App Installer feeds from the package that the release pipeline
/// has already signed and verified with SignTool. Reading the signature envelope here
/// does not replace cryptographic signature verification by the signing job or Windows.
/// </summary>
public static class AppInstallerFeedBuilder
{
    public const string AppInstallerNamespace = "http://schemas.microsoft.com/appx/appinstaller/2018";
    public const string PackageManifestNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    public const string ArtifactName = "WinVaultWarden";
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumSignatureBytes = 16 * 1024 * 1024;
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private const string Repository = "https://github.com/moooyo/WinVaultWarden";

    public static AppInstallerExpectedIdentity ReadExpectedIdentity(Stream repositoryManifest)
    {
        ArgumentNullException.ThrowIfNull(repositoryManifest);
        var identity = ReadManifest(repositoryManifest).Root?
            .Element(XName.Get("Identity", PackageManifestNamespace))
            ?? throw new InvalidDataException("The repository manifest must contain one package identity.");
        return new(RequiredAttribute(identity, "Name"), RequiredAttribute(identity, "Publisher"));
    }

    /// <summary>
    /// Validates normalized release inputs even when the release is a prerelease.
    /// Prereleases are valid artifacts but must not publish the stable update feeds.
    /// </summary>
    public static bool IsStableRelease(string releaseTag, string releaseVersion, string manifestVersion)
    {
        var release = ParseRelease(releaseTag, releaseVersion, manifestVersion);
        return !release.IsPrerelease;
    }

    public static AppInstallerFeed Build(
        Stream signedPackage,
        string packageAssetName,
        string releaseTag,
        string releaseVersion,
        string manifestVersion,
        AppInstallerExpectedIdentity expectedIdentity,
        string architecture)
    {
        ArgumentNullException.ThrowIfNull(signedPackage);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        if (architecture is not ("x64" or "arm64"))
            throw new ArgumentException("Only x64 and arm64 release feeds are supported.", nameof(architecture));
        var release = ParseRelease(releaseTag, releaseVersion, manifestVersion);
        if (release.IsPrerelease)
            throw new InvalidOperationException("Prereleases must not replace the stable update feeds.");
        var expectedAssetName = $"{ArtifactName}-{releaseVersion}-{architecture}.msix";
        if (!string.Equals(packageAssetName, expectedAssetName, StringComparison.Ordinal))
            throw new InvalidDataException("The signed package asset name does not match the release metadata.");
        if (string.IsNullOrWhiteSpace(expectedIdentity.Name) || string.IsNullOrWhiteSpace(expectedIdentity.Publisher))
            throw new ArgumentException("An explicit expected package identity is required.", nameof(expectedIdentity));

        var identity = ReadSignedPackageIdentity(signedPackage);
        if (identity.Name != expectedIdentity.Name || identity.Publisher != expectedIdentity.Publisher
            || identity.Architecture != architecture || identity.Version != release.PackageVersion)
            throw new InvalidDataException("The signed package identity does not match the expected release identity, architecture and version.");

        var fileName = $"{ArtifactName}-{architecture}.appinstaller";
        var feedUri = new Uri($"{Repository}/releases/latest/download/{fileName}");
        var packageUri = new Uri($"{Repository}/releases/download/{Uri.EscapeDataString(releaseTag)}/{Uri.EscapeDataString(expectedAssetName)}");
        var feedVersion = $"1.{release.PackageVersion.Major}.{release.PackageVersion.Minor}.{release.PackageVersion.Build}";
        XNamespace ns = AppInstallerNamespace;
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "AppInstaller",
                new XAttribute("Version", feedVersion),
                new XAttribute("Uri", feedUri.AbsoluteUri),
                new XElement(ns + "MainPackage",
                    new XAttribute("Name", identity.Name),
                    new XAttribute("Publisher", identity.Publisher),
                    new XAttribute("Version", identity.Version.ToString(4)),
                    new XAttribute("ProcessorArchitecture", identity.Architecture),
                    new XAttribute("Uri", packageUri.AbsoluteUri)),
                new XElement(ns + "UpdateSettings",
                    new XElement(ns + "OnLaunch", new XAttribute("HoursBetweenUpdateChecks", "24")),
                    new XElement(ns + "AutomaticBackgroundTask"))));

        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            NewLineChars = "\n",
            CloseOutput = false,
        }))
        {
            document.Save(writer);
        }
        return new(fileName, Encoding.UTF8.GetString(buffer.ToArray()), feedUri, packageUri, identity);
    }

    private static AppInstallerPackageIdentity ReadSignedPackageIdentity(Stream package)
    {
        if (!package.CanRead || !package.CanSeek || package.Length is <= 0 or > MaximumPackageBytes)
            throw new InvalidDataException("The signed MSIX must be a readable, seekable package within the size limit.");
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 100_000)
            throw new InvalidDataException("The MSIX contains too many entries.");
        var manifests = archive.Entries.Where(entry =>
            entry.FullName.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase)).ToArray();
        var signatures = archive.Entries.Where(entry =>
            entry.FullName.Equals("AppxSignature.p7x", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1 || manifests[0].FullName != "AppxManifest.xml"
            || manifests[0].Length is <= 0 or > MaximumManifestBytes
            || signatures.Length != 1 || signatures[0].FullName != "AppxSignature.p7x"
            || signatures[0].Length is <= 0 or > MaximumSignatureBytes)
            throw new InvalidDataException("The MSIX must contain one canonical manifest and signature envelope within their size limits.");

        using var source = manifests[0].Open();
        var identity = ReadManifest(source).Root!
            .Element(XName.Get("Identity", PackageManifestNamespace))!;
        var versionText = RequiredAttribute(identity, "Version");
        if (!TryPackageVersion(versionText, out var version))
            throw new InvalidDataException("The package identity must contain a canonical four-part version.");
        return new(
            RequiredAttribute(identity, "Name"),
            RequiredAttribute(identity, "Publisher"),
            RequiredAttribute(identity, "ProcessorArchitecture"),
            version!);
    }

    private static XDocument ReadManifest(Stream source)
    {
        using var reader = XmlReader.Create(source, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumManifestBytes,
            IgnoreComments = true,
            CloseInput = false,
        });
        var document = XDocument.Load(reader);
        XNamespace ns = PackageManifestNamespace;
        if (document.Root?.Name != ns + "Package" || document.Root.Elements(ns + "Identity").Count() != 1)
            throw new InvalidDataException("The manifest must have one Windows package identity.");
        return document;
    }

    private static string RequiredAttribute(XElement element, string name)
    {
        var value = (string?)element.Attribute(name);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl))
            throw new InvalidDataException($"The package identity has an invalid {name} attribute.");
        return value;
    }

    private static ReleaseVersion ParseRelease(string releaseTag, string releaseVersion, string manifestVersion)
    {
        if (string.IsNullOrEmpty(releaseTag) || releaseTag.Length > 128
            || string.IsNullOrEmpty(releaseVersion) || releaseVersion.Length > 128)
            throw new ArgumentException("A bounded release tag and normalized version are required.");
        var normalizedTag = releaseTag.StartsWith('v') ? releaseTag[1..] : releaseTag;
        if (!normalizedTag.Equals(releaseVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The release tag does not match its normalized version.");
        var metadata = normalizedTag.Split('+');
        if (metadata.Length > 2 || (metadata.Length == 2 && !ValidIdentifiers(metadata[1], allowNumericLeadingZeroes: true)))
            throw new InvalidDataException("The release build metadata is not valid SemVer.");
        var release = metadata[0].Split('-', 2);
        if (release.Length == 2 && !ValidIdentifiers(release[1], allowNumericLeadingZeroes: false))
            throw new InvalidDataException("The release prerelease identifiers are not valid SemVer.");
        var core = release[0].Split('.');
        if (core.Length != 3 || !TryComponent(core[0], out var major)
            || !TryComponent(core[1], out var minor) || !TryComponent(core[2], out var build))
            throw new InvalidDataException("The release core version is not a valid MSIX-compatible SemVer.");
        var packageVersion = new Version(major, minor, build, 0);
        if (manifestVersion != packageVersion.ToString(4))
            throw new InvalidDataException("The release manifest version must match the release core with revision zero.");
        return new(packageVersion, release.Length == 2);
    }

    private static bool ValidIdentifiers(string value, bool allowNumericLeadingZeroes) =>
        value.Split('.').All(identifier => identifier.Length > 0
            && identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            && (allowNumericLeadingZeroes || identifier.Length == 1 || identifier[0] != '0' || !identifier.All(char.IsAsciiDigit)));

    private static bool TryComponent(string text, out int component)
    {
        component = 0;
        return text.Length is > 0 and <= 5 && (text.Length == 1 || text[0] != '0')
            && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out component)
            && component <= ushort.MaxValue;
    }

    private static bool TryPackageVersion(string value, out Version? version)
    {
        version = null;
        var parts = value.Split('.');
        if (parts.Length != 4)
            return false;
        var numbers = new int[4];
        for (var index = 0; index < parts.Length; index++)
            if (!TryComponent(parts[index], out numbers[index]))
                return false;
        version = new(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    private sealed record ReleaseVersion(Version PackageVersion, bool IsPrerelease);
}
