using System.Text;
using Core.Updates;

try
{
    var required = new HashSet<string>(StringComparer.Ordinal)
    {
        "--artifacts", "--repository-manifest", "--release-tag", "--release-version",
        "--core-version", "--manifest-version", "--is-prerelease",
    };
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    if (args.Length != required.Count * 2)
        throw new ArgumentException("Provide each documented generator option exactly once.");
    for (var index = 0; index < args.Length; index += 2)
    {
        if (!required.Contains(args[index]) || !options.TryAdd(args[index], args[index + 1]))
            throw new ArgumentException("An unknown or duplicate generator option was provided.");
    }

    var releaseTag = options["--release-tag"];
    var releaseVersion = options["--release-version"];
    var manifestVersion = options["--manifest-version"];
    var stableTag = AppInstallerFeedBuilder.IsStableRelease(releaseTag, releaseVersion, manifestVersion);
    if (manifestVersion != $"{options["--core-version"]}.0")
        throw new InvalidDataException("The normalized core version does not match the manifest version.");
    var eventPrerelease = options["--is-prerelease"] switch
    {
        "true" => true,
        "false" => false,
        _ => throw new ArgumentException("The prerelease flag must be exactly true or false."),
    };
    var artifacts = Path.GetFullPath(options["--artifacts"]);
    if (!Directory.Exists(artifacts))
        throw new DirectoryNotFoundException("The signed release artifact directory does not exist.");
    if (!stableTag || eventPrerelease)
    {
        foreach (var architecture in new[] { "x64", "arm64" })
            if (File.Exists(Path.Combine(artifacts, $"{AppInstallerFeedBuilder.ArtifactName}-{architecture}.appinstaller")))
                throw new InvalidDataException("A prerelease output directory must not contain stable App Installer feeds.");
        Console.WriteLine("Prerelease detected. No stable App Installer feeds were generated.");
        return 0;
    }

    using var repositoryManifest = File.OpenRead(options["--repository-manifest"]);
    var expectedIdentity = AppInstallerFeedBuilder.ReadExpectedIdentity(repositoryManifest);
    var feeds = new List<AppInstallerFeed>();
    foreach (var architecture in new[] { "x64", "arm64" })
    {
        var assetName = $"{AppInstallerFeedBuilder.ArtifactName}-{releaseVersion}-{architecture}.msix";
        using var signedPackage = File.OpenRead(Path.Combine(artifacts, assetName));
        feeds.Add(AppInstallerFeedBuilder.Build(signedPackage, assetName, releaseTag,
            releaseVersion, manifestVersion, expectedIdentity, architecture));
    }

    // Both architectures are validated before any feed is staged for publication.
    foreach (var feed in feeds)
    {
        var destination = Path.Combine(artifacts, feed.FileName);
        var temporary = Path.Combine(artifacts, $".{feed.FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, feed.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        Console.WriteLine($"Generated {feed.FileName} for signed package {feed.PackageIdentity.Version} ({feed.PackageIdentity.Architecture}).");
    }
    return 0;
}
catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or InvalidOperationException
    or System.Xml.XmlException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"App Installer feed generation failed: {exception.Message}");
    return 1;
}
