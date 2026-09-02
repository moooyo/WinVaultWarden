using Xunit;

namespace App.Tests;

public sealed class DuoWebViewSecuritySourceTests
{
    [Fact]
    public void Service_UsesDedicatedInPrivateProfileAndDisablesAmbientCapabilities()
    {
        var source = ReadServiceSource();

        Assert.Contains("AppStorageLayout.CreateDefault().RootPath", source, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(AppStorageLayout.CreateDefault().RootPath, \"WebView2\")", source, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2Environment.CreateWithOptionsAsync(", source, StringComparison.Ordinal);
        Assert.Contains("GetUserDataFolder()", source, StringComparison.Ordinal);
        Assert.Contains("CreateCoreWebView2ControllerOptions()", source, StringComparison.Ordinal);
        Assert.Contains("ProfileName = \"WinVaultWarden-Duo\"", source, StringComparison.Ordinal);
        Assert.Contains("IsInPrivateModeEnabled = true", source, StringComparison.Ordinal);
        Assert.Contains("Profile.IsInPrivateModeEnabled", source, StringComparison.Ordinal);
        Assert.Contains("AreHostObjectsAllowed = false", source, StringComparison.Ordinal);
        Assert.Contains("IsWebMessageEnabled = false", source, StringComparison.Ordinal);
        Assert.Contains("AreDefaultScriptDialogsEnabled = false", source, StringComparison.Ordinal);
        Assert.Contains("IsGeneralAutofillEnabled = false", source, StringComparison.Ordinal);
        Assert.Contains("IsPasswordAutosaveEnabled = false", source, StringComparison.Ordinal);
        Assert.Contains("AreBrowserAcceleratorKeysEnabled = false", source, StringComparison.Ordinal);
        Assert.Contains("AreDevToolsEnabled = false", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_RejectsSecondaryBrowserSurfacesAndUsesFailClosedNavigationPolicy()
    {
        var source = ReadServiceSource();

        Assert.Contains("DuoNavigationPolicy.Evaluate", source, StringComparison.Ordinal);
        Assert.Contains("Interlocked.CompareExchange", source, StringComparison.Ordinal);
        Assert.Contains("core.NewWindowRequested +=", source, StringComparison.Ordinal);
        Assert.Contains("core.DownloadStarting +=", source, StringComparison.Ordinal);
        Assert.Contains("core.PermissionRequested +=", source, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = true", source, StringComparison.Ordinal);
        Assert.Contains("args.Handled = true", source, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2PermissionState.Deny", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_StopsUnhooksClearsProfileAndClosesWebView()
    {
        var source = ReadServiceSource();

        Assert.Contains("core.Stop()", source, StringComparison.Ordinal);
        Assert.Contains("core.NavigationStarting -=", source, StringComparison.Ordinal);
        Assert.Contains("core.NewWindowRequested -=", source, StringComparison.Ordinal);
        Assert.Contains("core.DownloadStarting -=", source, StringComparison.Ordinal);
        Assert.Contains("core.PermissionRequested -=", source, StringComparison.Ordinal);
        Assert.Contains("core.ProcessFailed -=", source, StringComparison.Ordinal);
        Assert.Contains("ClearBrowsingDataAsync", source, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2BrowsingDataKinds.AllProfile", source, StringComparison.Ordinal);
        Assert.Contains("webView.Close()", source, StringComparison.Ordinal);

        var stop = source.IndexOf("core.Stop()", StringComparison.Ordinal);
        var unhook = source.IndexOf("core.NavigationStarting -=", StringComparison.Ordinal);
        var profileClear = source.IndexOf("ClearBrowsingDataAsync", StringComparison.Ordinal);
        var close = source.IndexOf("webView.Close()", StringComparison.Ordinal);
        Assert.True(stop < unhook && unhook < profileClear && profileClear < close);
    }

    private static string ReadServiceSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(
                directory.FullName,
                "src",
                "App",
                "Services",
                "DuoWebViewTwoFactorService.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate DuoWebViewTwoFactorService.cs.");
    }
}
