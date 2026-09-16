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
        var terminationHost = Read("src", "App", "Services", "NotificationsHost.cs");
        var settings = Read("src", "App", "ViewModels", "SettingsViewModel.cs");
        Assert.Contains("SessionTerminationHost", services);
        Assert.Contains("AppLifecycleIntent.RemoteLogout", terminationHost);
        Assert.Contains("SessionTerminationReason.PinPolicyEnforcementFailed", terminationHost);
        Assert.Contains("AppLifecycleIntent.CredentialChanged", settings);
        Assert.DoesNotContain("NavigateToLogin()", Read("src", "App", "Views", "SettingsPage.xaml.cs"));
    }

    [Fact]
    public void TerminationDispatchFailureRetriesDurableLogoutThenFailsClosedSynchronously()
    {
        var services = Read("src", "App", "Services", "ServiceConfiguration.cs");
        var dispatcher = Read("src", "App", "Services", "IUiDispatcher.cs");
        var implementation = Read("src", "App", "Services", "UiDispatcher.cs");

        Assert.Contains("bool Enqueue(Action action)", dispatcher);
        Assert.Contains("return queue is not null && queue.TryEnqueue", implementation);
        Assert.Contains("if (!queued)", services);
        Assert.Contains("notice.InvalidatedSession", services);
        Assert.Contains(".GetAwaiter()", services);
        Assert.Contains("lifecycle.FailClosedSynchronously()", services);
    }

    [Fact]
    public void NotificationSnapshotRefresh_UpdatesVaultContentAndNavigationShell()
    {
        var services = Read("src", "App", "Services", "ServiceConfiguration.cs");
        var window = Read("src", "App", "MainWindow.xaml.cs");

        Assert.Contains("MainWindow?.RefreshVaultSnapshot()", services);
        Assert.Contains("public void RefreshVaultSnapshot()", window);
        Assert.Contains("PopulateFolderNavigation();", window);
        Assert.Contains("RefreshVaultList();", window);
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
        Assert.Contains("frame.CacheSize = 0", source);
        Assert.Contains("frame.Content = null", source);
        Assert.Contains("frame.BackStack.Clear()", source);
        Assert.Contains("frame.ForwardStack.Clear()", source);
    }

    [Fact]
    public void VaultNavigation_PreservesCurrentDraftWhileCreateIsInFlightOrPending()
    {
        var source = Read("src", "App", "MainWindow.xaml.cs");
        var gate = source.IndexOf(".HasPendingOrInFlightCipherCreate()", StringComparison.Ordinal);
        var reuse = source.IndexOf("currentVaultPage!.ViewModel.SelectFilterByTag(tag)", StringComparison.Ordinal);
        var navigate = source.IndexOf("_nav.GoToVault(tag)", StringComparison.Ordinal);

        Assert.True(gate >= 0);
        Assert.True(reuse > gate);
        Assert.True(navigate > reuse);
    }

    [Fact]
    public void PendingCreate_BlocksNavigationAwayFromVaultButAllowsSafeActions()
    {
        var source = Read("src", "App", "MainWindow.xaml.cs");
        var guard = source.IndexOf(
            "currentVaultPage!.ViewModel.EditorDraft is not null",
            StringComparison.Ordinal);
        var sendNavigation = source.IndexOf("tag.StartsWith(\"send:\"", StringComparison.Ordinal);

        Assert.True(guard >= 0);
        Assert.True(sendNavigation > guard);
        Assert.Contains("tag is not (\"gen\" or \"lock\" or \"logout\")", source);
        Assert.Contains("Title = \"保存结果待确认\"", source);
        Assert.Contains("请先同步确认新条目的保存结果，再离开密码库。", source);
        Assert.Contains("CloseButtonText = \"知道了\"", source);
        Assert.Contains("DialogHelper.ShowTrackedAsync(dialog)", source);
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
