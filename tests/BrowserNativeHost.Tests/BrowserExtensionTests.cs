using System.Text.Json;
using Xunit;

namespace BrowserNativeHost.Tests;

public class BrowserExtensionTests
{
    [Fact]
    public void Manifest_InjectsWebAuthnBridgeAndAllowsNativeMessaging()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindRepoFile("browser-extension", "manifest.json")));
        var root = document.RootElement;

        Assert.Equal(3, root.GetProperty("manifest_version").GetInt32());
        Assert.Contains(root.GetProperty("permissions").EnumerateArray(),
            permission => permission.GetString() == "nativeMessaging");

        var contentScript = Assert.Single(root.GetProperty("content_scripts").EnumerateArray());
        Assert.Equal("document_start", contentScript.GetProperty("run_at").GetString());
        Assert.True(contentScript.GetProperty("all_frames").GetBoolean());
        Assert.Contains(contentScript.GetProperty("js").EnumerateArray(),
            script => script.GetString() == "src/content-webauthn.js");

        var resource = Assert.Single(root.GetProperty("web_accessible_resources").EnumerateArray());
        Assert.Contains(resource.GetProperty("resources").EnumerateArray(),
            script => script.GetString() == "src/page-webauthn.js");
    }

    [Fact]
    public void PageScript_InterceptsCreateGetAndFallsBackWhenHostIsUnavailable()
    {
        var script = File.ReadAllText(FindRepoFile("browser-extension", "src", "page-webauthn.js"));

        Assert.Contains("credentials.create", script);
        Assert.Contains("credentials.get", script);
        Assert.Contains("\"passkey.create\"", script);
        Assert.Contains("\"passkey.get\"", script);
        Assert.Contains("native_host_unavailable", script);
        Assert.Contains("not_implemented", script);
        Assert.Contains("vault_locked", script);
        Assert.Contains("credential_not_found", script);
        Assert.Contains("nativeCreate(options)", script);
        Assert.Contains("nativeGet(options)", script);
    }

    [Fact]
    public void ContentScript_RelaysOnlyPageBridgeMessagesToNativeHost()
    {
        var script = File.ReadAllText(FindRepoFile("browser-extension", "src", "content-webauthn.js"));

        Assert.Contains("winvaultwarden-page", script);
        Assert.Contains("winvaultwarden-content", script);
        Assert.Contains("winvaultwarden-native", script);
        Assert.Contains("chrome.runtime.sendMessage", script);
        Assert.Contains("window.postMessage", script);
    }

    [Fact]
    public void NativeMessagingInstallScript_PublishesManifestAndRegistersChromeEdge()
    {
        var script = File.ReadAllText(FindRepoFile(
            "browser-extension",
            "native-messaging",
            "install-native-host.ps1"));

        Assert.Contains("BrowserNativeHost.csproj", script);
        Assert.Contains("ConvertTo-Json", script);
        Assert.Contains("chrome-extension://$ExtensionId/", script);
        Assert.Contains(@"HKCU\Software\Google\Chrome\NativeMessagingHosts", script);
        Assert.Contains(@"HKCU\Software\Microsoft\Edge\NativeMessagingHosts", script);
        Assert.Contains("reg.exe add", script);
    }

    [Fact]
    public void PasskeyGetTestPage_RequestsDiscoverableLocalhostCredential()
    {
        var page = File.ReadAllText(FindRepoFile("browser-extension", "test-pages", "passkey-get.html"));

        Assert.Contains("navigator.credentials.get", page);
        Assert.Contains("rpIdInput.value = location.hostname", page);
        Assert.Contains("allowCredentials", page);
        Assert.Contains("localhost", page);
        Assert.Contains("credential.toJSON()", page);
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
