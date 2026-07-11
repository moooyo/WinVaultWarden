using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace BrowserNativeHost.Tests;

public sealed class NativeHostManifestTests
{
    private const string ChromeId = "ehakaifefnodlknfnekcnljhljcaoclb";
    private const string EdgeId = "icobpiocjfkfebioicnpmccolpofhpik";

    [Fact]
    public void ChromeAndEdge_UseFixedDistinctIdsAndMatchingOrigins()
    {
        using var identities = JsonDocument.Parse(
            File.ReadAllText(FindRepoFile("browser-extension", "extension-identities.json")));
        var chrome = identities.RootElement.GetProperty("chrome");
        var edge = identities.RootElement.GetProperty("edge");

        Assert.Equal(ChromeId, chrome.GetProperty("id").GetString());
        Assert.Equal(EdgeId, edge.GetProperty("id").GetString());

        Assert.Equal(chrome.GetProperty("id").GetString(),
            ChromiumId(chrome.GetProperty("publicKey").GetString()!));
        Assert.Equal(edge.GetProperty("id").GetString(),
            ChromiumId(edge.GetProperty("publicKey").GetString()!));
        Assert.NotEqual(chrome.GetProperty("id").GetString(), edge.GetProperty("id").GetString());

        AssertTemplate("chrome", chrome.GetProperty("id").GetString()!);
        AssertTemplate("edge", edge.GetProperty("id").GetString()!);
    }

    [Fact]
    public void ExtensionManifests_EmbedKeysThatRecomputeToFixedIds()
    {
        using var identities = JsonDocument.Parse(
            File.ReadAllText(FindRepoFile("browser-extension", "extension-identities.json")));
        var chromeKey = identities.RootElement.GetProperty("chrome").GetProperty("publicKey").GetString()!;
        var edgeKey = identities.RootElement.GetProperty("edge").GetProperty("publicKey").GetString()!;

        using var chromeManifest = JsonDocument.Parse(
            File.ReadAllText(FindRepoFile("browser-extension", "manifest.json")));
        using var edgeManifest = JsonDocument.Parse(
            File.ReadAllText(FindRepoFile("browser-extension", "manifest.edge.json")));

        var chromeManifestKey = chromeManifest.RootElement.GetProperty("key").GetString()!;
        var edgeManifestKey = edgeManifest.RootElement.GetProperty("key").GetString()!;

        // The embedded manifest key is the sole source of truth for the loaded extension id;
        // recompute it rather than trusting a hand-written constant.
        Assert.Equal(chromeKey, chromeManifestKey);
        Assert.Equal(edgeKey, edgeManifestKey);
        Assert.Equal(ChromeId, ChromiumId(chromeManifestKey));
        Assert.Equal(EdgeId, ChromiumId(edgeManifestKey));
        Assert.NotEqual(chromeManifestKey, edgeManifestKey);
    }

    [Fact]
    public void LegacySharedTemplate_IsRemoved()
    {
        // Scope to this worktree's root (nearest ancestor with global.json); walking further up
        // would reach a sibling checkout that still carries the legacy template.
        var legacy = Path.Combine(
            FindRepoRoot(),
            "browser-extension",
            "native-messaging",
            "com.winvaultwarden.browser.json.template");

        Assert.False(File.Exists(legacy), $"Legacy shared template should be deleted: {legacy}");
    }

    private static void AssertTemplate(string browser, string id)
    {
        using var template = JsonDocument.Parse(File.ReadAllText(FindRepoFile(
            "browser-extension",
            "native-messaging",
            $"com.winvaultwarden.browser.{browser}.json.template")));
        var root = template.RootElement;

        Assert.Equal("com.winvaultwarden.browser", root.GetProperty("name").GetString());
        Assert.Equal("%WINVAULTWARDEN_NATIVE_HOST_PATH%", root.GetProperty("path").GetString());
        Assert.Equal("stdio", root.GetProperty("type").GetString());

        var origin = Assert.Single(root.GetProperty("allowed_origins").EnumerateArray());
        Assert.Equal($"chrome-extension://{id}/", origin.GetString());

        Assert.DoesNotContain("%CHROME_EXTENSION_ID%", root.GetRawText());
    }

    private static string ChromiumId(string publicKey)
    {
        var hash = SHA256.HashData(Convert.FromBase64String(publicKey));
        const string alphabet = "abcdefghijklmnop";
        return string.Concat(hash[..16].SelectMany(value => new[]
        {
            alphabet[value >> 4],
            alphabet[value & 0x0f],
        }));
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

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root (global.json).");
    }
}
