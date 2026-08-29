using Xunit;

namespace App.Tests;

// Task 7: passkey bridge lifecycle must ride the App/vault lifecycle, not a bare preference. This
// reads the composition/window/dialog source (WinUI files that cannot be compiled headless) and
// asserts the wiring: the lifecycle coordinator stops the bridge with the rest of the background,
// every approval dialog goes through the tracked DialogHelper (so Lock force-hides + clears it),
// and the bridge only (re)starts after the vault is unlocked/ready — never from the locked login page.
public sealed class BrowserPasskeyLifecycleWiringTests
{
    [Fact]
    public void StopBackgroundAsync_AlsoStopsPasskeyBridge()
    {
        var source = Read("src", "App", "Services", "ServiceConfiguration.cs");
        Assert.Contains("StopBackgroundAsync", source);
        // The passkey bridge is torn down together with the notifications host when the session ends.
        Assert.Contains("GetRequiredService<PasskeyBridgeServer>().StopAsync()", source);
    }

    [Fact]
    public void ApprovalDialogs_OnlyShownThroughTrackedDialogHelper()
    {
        var source = Read("src", "App", "Services", "PasskeyApprovalDialogService.cs");
        Assert.Contains("DialogHelper.ShowTrackedAsync", source);
        // A raw ContentDialog.ShowAsync() would escape lifecycle tracking / sensitive clearing.
        Assert.DoesNotContain(".ShowAsync(", source);
    }

    [Fact]
    public void UiDispatchCompletion_IsRaceSafeWhenCancellationWins()
    {
        var source = Read("src", "App", "Services", "PasskeyApprovalDialogService.cs");
        var method = ExtractMethodBody(source, "private static async Task<T> InvokeOnUiAsync<T>(");

        Assert.Contains("result.TrySetResult", method);
        Assert.Contains("result.TrySetException", method);
        Assert.DoesNotContain("result.SetResult", method);
        Assert.DoesNotContain("result.SetException", method);
    }

    [Fact]
    public void ShowVault_StartsBridge_AfterSessionReady_PerSetting()
    {
        var source = Read("src", "App", "MainWindow.xaml.cs");
        var showVault = ExtractMethodBody(source, "public bool ShowVault(VaultSessionLease lease)");
        Assert.Contains("TryActivateSession", showVault);
        Assert.Contains("TryRunIfUnlocked", showVault);
        Assert.Contains("AllowBrowserIntegration", showVault);
        Assert.Contains("PasskeyBridgeServer", showVault);
        Assert.Contains(".Start()", showVault);
    }

    [Fact]
    public void AppLaunch_DoesNotStartBridgeFromPreferenceAlone()
    {
        var source = Read("src", "App", "App.xaml.cs");
        // OnLaunched must no longer start the bridge just because the preference is on: the locked
        // login page must not listen. That gating now lives in ShowVault (session ready).
        Assert.DoesNotContain("AppPreferences.Current.AllowBrowserIntegration", source);
    }

    [Fact]
    public void SettingsChange_OnlyStartsBridge_WhenVaultUnlocked()
    {
        var source = Read("src", "App", "App.xaml.cs");
        Assert.Contains("ApplyBrowserIntegrationSettingAsync", source);
        Assert.Contains("VaultState.Unlocked", source);
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method not found: {signature}");
        var brace = source.IndexOf('{', start);
        Assert.True(brace >= 0, $"Method body not found: {signature}");
        var depth = 0;
        for (var i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[brace..(i + 1)];
        }
        throw new InvalidOperationException($"Unbalanced braces after {signature}");
    }

    private static string Read(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
