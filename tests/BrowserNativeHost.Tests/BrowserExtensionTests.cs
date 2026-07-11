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

        // The passkey bridge is intentionally headless: no toolbar/action surface, no options
        // page, and it never touches stored passwords. Lock those out so scope creep is a red test.
        Assert.False(root.TryGetProperty("action", out _));
        Assert.False(root.TryGetProperty("options_page", out _));
        Assert.False(root.TryGetProperty("options_ui", out _));
        Assert.DoesNotContain("password", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("no_credential", script);
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
        // Content script pins the real frame origin (isolated world) over the page-reported one.
        Assert.Contains("request.payload.origin = event.origin", script);
    }

    [Fact]
    public void NativeMessagingInstallScript_RegistersChromeEdgeFromAppRootWithoutBuilding()
    {
        var script = File.ReadAllText(FindRepoFile(
            "browser-extension",
            "native-messaging",
            "install-native-host.ps1"));

        // Registers both browsers from their own pinned templates.
        Assert.Contains(@"HKCU\Software\Google\Chrome\NativeMessagingHosts", script);
        Assert.Contains(@"HKCU\Software\Microsoft\Edge\NativeMessagingHosts", script);
        Assert.Contains("reg.exe add", script);
        Assert.Contains("com.winvaultwarden.browser.chrome.json.template", script);
        Assert.Contains("com.winvaultwarden.browser.edge.json.template", script);

        // Points at the shipped Host next to App.exe and fills the manifest path token.
        Assert.Contains("BrowserNativeHost.exe", script);
        Assert.Contains("%WINVAULTWARDEN_NATIVE_HOST_PATH%", script);

        // The installer no longer builds or copies artifacts, and no longer takes an extension id:
        // identities are fixed by the shipped templates.
        Assert.DoesNotContain("dotnet publish", script);
        Assert.DoesNotContain("BrowserNativeHost.csproj", script);
        Assert.DoesNotContain("$ExtensionId", script);
        Assert.DoesNotContain("artifacts", script);
    }

    [Fact]
    public void NativeMessagingInstallScript_IsPortableOnlyAndRefusesMsix()
    {
        var script = File.ReadAllText(FindRepoFile(
            "browser-extension",
            "native-messaging",
            "install-native-host.ps1"));

        // User decision: browser integration is portable-only. The installer must detect an MSIX
        // package location (WindowsApps or an AppxManifest.xml alongside the Host) and refuse it
        // loudly with a clear message, never touching the registry or writing a manifest.
        Assert.Contains("WindowsApps", script);
        Assert.Contains("MSIX 不支持浏览器集成，请使用便携版", script);
        Assert.Contains("throw", script);

        // No MSIX fallback: the installer must not discover or register an installed package.
        Assert.DoesNotContain("Get-AppxPackage", script);
        Assert.DoesNotContain("InstallLocation", script);
    }

    [Fact]
    public void NativeMessagingUninstallScript_RemovesBothBrowserManifestsIdempotently()
    {
        var script = File.ReadAllText(FindRepoFile(
            "browser-extension",
            "native-messaging",
            "uninstall-native-host.ps1"));

        Assert.Contains(@"HKCU\Software\Google\Chrome\NativeMessagingHosts", script);
        Assert.Contains(@"HKCU\Software\Microsoft\Edge\NativeMessagingHosts", script);
        Assert.Contains("reg.exe delete", script);
        // Cleans each browser's own manifest directory rather than a single shared file.
        Assert.Contains("chrome", script);
        Assert.Contains("edge", script);
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
