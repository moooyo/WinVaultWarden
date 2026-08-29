using Xunit;

namespace App.Tests;

public sealed class LifecycleWiringSourceTests
{
    [Fact]
    public void CompositionRoot_RegistersExactlyOneLifecycleCoordinator()
    {
        var source = Read("src", "App", "Services", "ServiceConfiguration.cs");
        Assert.Equal(1, Count(source, "AddSingleton<IAppLifecycleCoordinator>"));
        Assert.DoesNotContain("AddSingleton<AppLifecycleCoordinator>", source);
    }

    [Fact]
    public void WindowEntries_DoNotCallAuthLockOrLogoutDirectly()
    {
        var source = Read("src", "App", "MainWindow.xaml.cs");
        Assert.DoesNotContain("GetRequiredService<IAuthService>().LockAsync", source);
        Assert.DoesNotContain("GetRequiredService<IAuthService>().LogoutAsync", source);
        Assert.Contains("AppLifecycleIntent.Lock", source);
        Assert.Contains("AppLifecycleIntent.Logout", source);
        Assert.Contains("AppLifecycleIntent.Exit", source);
    }

    [Fact]
    public void SensitiveUiGate_DisablesVaultNavigationWithoutBlockingLogin()
    {
        var source = Read("src", "App", "MainWindow.xaml.cs");
        Assert.Contains("Nav.IsEnabled = enabled", source);
        Assert.DoesNotContain("LoginHost.IsEnabled", source);
        Assert.DoesNotContain("LoginFrame.IsEnabled", source);
    }

    [Fact]
    public void MainWindow_UsesProductTitle()
    {
        var xaml = Read("src", "App", "MainWindow.xaml");
        Assert.Contains("Title=\"WinVaultWarden\"", xaml);
    }

    [Fact]
    public void RemoteLogoutAndCredentialChanges_UseCoordinator()
    {
        var services = Read("src", "App", "Services", "ServiceConfiguration.cs");
        var settings = Read("src", "App", "ViewModels", "SettingsViewModel.cs");
        Assert.Contains("AppLifecycleIntent.RemoteLogout", services);
        Assert.Contains("AppLifecycleIntent.CredentialChanged", settings);
        Assert.DoesNotContain("NavigateToLogin()", Read("src", "App", "Views", "SettingsPage.xaml.cs"));
    }

    [Fact]
    public void PreviousLayer_AccountMutations_DoNotLogoutDirectly()
    {
        var account = Read("src", "Vault", "AccountService.cs");
        Assert.DoesNotContain("_auth.LogoutAsync", account);
    }

    [Fact]
    public void FrameReset_ClearsContentAndBothNavigationStacks()
    {
        var source = Read("src", "App", "Services", "FrameNavigationService.cs");
        Assert.Contains("frame.Content = null", source);
        Assert.Contains("frame.BackStack.Clear()", source);
        Assert.Contains("frame.ForwardStack.Clear()", source);
    }

    private static int Count(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;

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
