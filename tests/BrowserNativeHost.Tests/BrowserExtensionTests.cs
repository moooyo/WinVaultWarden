using System.Text.Json;
using Xunit;

namespace BrowserNativeHost.Tests;

public class BrowserExtensionTests
{
    [Fact]
    public void Manifests_DeclareMainAndIsolatedWorldsWithoutDynamicInjection()
    {
        // Both Chrome and Edge inject the same two declarative content scripts: the shim into the
        // page's MAIN world, the relay into the extension's ISOLATED world. There is no
        // web_accessible_resource (the old dynamic <script> injection surface) and no popup/options.
        foreach (var file in new[] { "manifest.json", "manifest.edge.json" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(FindRepoFile("browser-extension", file)));
            var root = document.RootElement;

            Assert.Equal(3, root.GetProperty("manifest_version").GetInt32());
            Assert.Contains(root.GetProperty("permissions").EnumerateArray(),
                permission => permission.GetString() == "nativeMessaging");

            var scripts = root.GetProperty("content_scripts").EnumerateArray().ToArray();
            Assert.Equal(2, scripts.Length);

            var main = scripts[0];
            var isolated = scripts[1];
            Assert.Equal("MAIN", main.GetProperty("world").GetString());
            Assert.Equal("ISOLATED", isolated.GetProperty("world").GetString());

            foreach (var script in scripts)
            {
                Assert.Equal("document_start", script.GetProperty("run_at").GetString());
                Assert.True(script.GetProperty("all_frames").GetBoolean());
            }

            Assert.Equal(
                new[] { "src/error-map.js", "src/request-coordinator.js", "src/page-webauthn.js" },
                main.GetProperty("js").EnumerateArray().Select(script => script.GetString()).ToArray());
            Assert.Equal(
                new[] { "src/request-coordinator.js", "src/content-webauthn.js" },
                isolated.GetProperty("js").EnumerateArray().Select(script => script.GetString()).ToArray());

            // The passkey bridge is intentionally headless: no dynamic injection surface, no toolbar
            // action, no options page, and it never touches stored passwords. Lock those out so any
            // scope creep is a red test.
            Assert.False(root.TryGetProperty("web_accessible_resources", out _));
            Assert.False(root.TryGetProperty("action", out _));
            Assert.False(root.TryGetProperty("options_page", out _));
            Assert.False(root.TryGetProperty("options_ui", out _));
            Assert.DoesNotContain("password", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PageScript_RunsInMainWorldAndFallsBackOnlyForClientNotHandled()
    {
        var script = File.ReadAllText(FindRepoFile("browser-extension", "src", "page-webauthn.js"));

        Assert.Contains("\"passkey.create\"", script);
        Assert.Contains("\"passkey.get\"", script);
        // Passive/silent get reaches the platform authenticator directly, never the bridge.
        Assert.Contains("conditional", script);
        Assert.Contains("silent", script);
        Assert.Contains("nativeCreate(options)", script);
        Assert.Contains("nativeGet(options)", script);
        // Fallback is driven solely by the shared, code-only error map (client_not_handled), and the
        // v1 request envelope never carries a page-dictated origin/crossOrigin.
        Assert.Contains("WinVaultWardenErrorMap", script);
        Assert.Contains("shouldFallback", script);
        Assert.Contains("WinVaultWardenRequestCoordinator", script);
        Assert.Contains("v: 1", script);
        Assert.DoesNotContain("payload.origin", script);
        Assert.DoesNotContain("crossOrigin", script);
    }

    [Fact]
    public void ContentScript_UsesPersistentPortAndStripsPageContext()
    {
        var script = File.ReadAllText(FindRepoFile("browser-extension", "src", "content-webauthn.js"));

        Assert.Contains("winvaultwarden-main", script);
        Assert.Contains("winvaultwarden-isolated", script);
        Assert.Contains("chrome.runtime.connect", script);
        Assert.Contains("winvaultwarden-passkey-frame-v1", script);
        // The isolated world computes the real Permissions Policy verdict and strips any page context.
        Assert.Contains("publicKeyCredentialsGetAllowed", script);
        Assert.Contains("permissionsPolicy", script);
        Assert.Contains("delete request.payload.origin", script);
        Assert.Contains("delete request.context", script);
        // No dynamic <script> injection, no one-shot messaging, no web-accessible host URL lookups.
        Assert.DoesNotContain("createElement", script);
        Assert.DoesNotContain("getURL", script);
        Assert.DoesNotContain("sendMessage", script);
        Assert.DoesNotContain("sendNativeMessage", script);
    }

    [Fact]
    public void Background_UsesPersistentNativePortAndBrowserAuthenticatedContext()
    {
        var background = File.ReadAllText(FindRepoFile("browser-extension", "src", "background.js"));

        // Trust is assigned from the browser sender, never the page; one persistent native port.
        Assert.Contains("onConnect", background);
        Assert.Contains("winvaultwarden-passkey-frame-v1", background);
        Assert.Contains("buildTrustedContext", background);
        Assert.Contains("port.sender", background);
        Assert.DoesNotContain("sendNativeMessage", background);

        var nativePort = File.ReadAllText(FindRepoFile("browser-extension", "src", "native-port.js"));
        Assert.Contains("connectNative", nativePort);
        Assert.Contains("com.winvaultwarden.browser", nativePort);
        // Lazy singleton: ping/pong readiness, then the two disconnect outcomes.
        Assert.Contains("\"ping\"", nativePort);
        Assert.Contains("client_not_handled", nativePort);
        Assert.Contains("operation_aborted", nativePort);

        var trusted = File.ReadAllText(FindRepoFile("browser-extension", "src", "trusted-context.js"));
        Assert.Contains("security_sender", trusted);
        Assert.Contains("frameOrigin", trusted);
        Assert.Contains("topOrigin", trusted);
        Assert.Contains("publicKeyCredentialsGetAllowed", trusted);
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

    [Theory]
    [InlineData("passkey-get.html", "navigator.credentials.get")]
    [InlineData("passkey-create.html", "navigator.credentials.create")]
    [InlineData("iframe-top.html", "public-key-credentials-get")]
    [InlineData("iframe-child.html", "navigator.credentials.get")]
    [InlineData("abort-deadline.html", "AbortController")]
    public void BrowserTestPage_CoversNamedScenario(string file, string marker)
    {
        var page = File.ReadAllText(
            FindRepoFile("browser-extension", "test-pages", file));
        Assert.Contains(marker, page);
        Assert.DoesNotContain("type=\"password\"", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptanceDocument_ListsBothBrowsersAndAllManualOnlyChecks()
    {
        var document = File.ReadAllText(
            FindRepoFile("docs", "browser-integration-acceptance.md"));
        Assert.Contains("仅适用于 portable 版", document);
        Assert.Contains("MSIX 负验收", document);
        Assert.Contains("无 Host", document);
        Assert.Contains("设置禁用", document);
        Assert.Contains("安装脚本 loud 拒绝", document);
        Assert.DoesNotContain("portable/MSIX", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MSIX 安装后，App↔Host", document);
    }

    [Theory]
    [InlineData("passkey-get.html")]
    [InlineData("passkey-create.html")]
    [InlineData("iframe-child.html")]
    [InlineData("abort-deadline.html")]
    public void BrowserTestPage_DoesNotSurfaceErrorMessage(string file)
    {
        var page = File.ReadAllText(
            FindRepoFile("browser-extension", "test-pages", file));

        Assert.DoesNotContain("error.message", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("${error.message}", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".message", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PasskeysBrowserIntegration_DocumentsConditionalSilentAfterPublicKeyOnlyForGet()
    {
        var document = File.ReadAllText(
            FindRepoFile("docs", "passkeys-browser-integration.md"));

        Assert.Contains("navigator.credentials.get()", document);
        Assert.Contains("has confirmed `options.publicKey` is present", document);
        Assert.Contains("`create()` has no mediation short-circuit", document);
        Assert.DoesNotContain("checks `options.mediation` before doing anything else", document);
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
