using Xunit;

namespace App.Tests;

public sealed class SettingsAccountDeletionFlowSourceTests
{
    [Fact]
    public void ClaimedDomainBlockRunsBeforeAnyDeleteDispatch()
    {
        var source = Read("src", "App", "Views", "SettingsPage.xaml.cs");
        var block = source.IndexOf(
            "AccountDeletionPreflightBlockReason.ClaimedByOrganization",
            StringComparison.Ordinal);
        var dispatch = source.IndexOf(
            "ViewModel.DeleteAccountAsync",
            StringComparison.Ordinal);

        Assert.True(block >= 0);
        Assert.True(dispatch > block);
        Assert.Contains("客户端不会发送删除请求", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FlowRequiresConsequencesAndExactEmailConfirmation()
    {
        var source = Read("src", "App", "Views", "SettingsPage.xaml.cs");

        Assert.Contains("DeleteAccountConsequencesDialog", source, StringComparison.Ordinal);
        Assert.Contains("DeleteAccountEmailConfirmationDialog", source, StringComparison.Ordinal);
        Assert.Contains("DeleteAccountEmailConfirmationBox", source, StringComparison.Ordinal);
        Assert.Contains("StringComparison.Ordinal", source, StringComparison.Ordinal);
        Assert.Contains("OwnerWarnings", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialBranchesCoverMasterPasswordOtpAndFreshLogin()
    {
        var source = Read("src", "App", "Views", "SettingsPage.xaml.cs");

        Assert.Contains("DeleteAccountMasterPasswordBox", source, StringComparison.Ordinal);
        Assert.Contains("DeleteAccountOtpBox", source, StringComparison.Ordinal);
        Assert.Contains("RequestAccountDeletionOtpAsync", source, StringComparison.Ordinal);
        Assert.Contains(
            "AccountDeletionCredentialRequirement.FreshOnlineAuthentication",
            source,
            StringComparison.Ordinal);
        Assert.Contains("需要重新在线登录", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDeletionOutcomeHasAnExplicitUiBranch()
    {
        var source = Read("src", "App", "Views", "SettingsPage.xaml.cs");

        Assert.Contains("AccountDeletionResult.Confirmed:", source, StringComparison.Ordinal);
        Assert.Contains("AccountDeletionResult.Rejected rejected:", source, StringComparison.Ordinal);
        Assert.Contains(
            "AccountDeletionResult.CancelledBeforeDispatch cancelled:",
            source,
            StringComparison.Ordinal);
        Assert.Contains("AccountDeletionResult.OutcomeUncertain:", source, StringComparison.Ordinal);
        Assert.Contains(
            "AccountDeletionResult.OutcomeUncertainLocalCleanupComplete:",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete:",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AccountDeletionResult.ConfirmedLocalCleanupIncomplete:",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalOutcomesTransitionImmediatelyWithoutRetrying()
    {
        var source = Read("src", "App", "Views", "SettingsPage.xaml.cs");

        Assert.DoesNotContain("ShowPendingAccountDeletionAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearLocalDataForPendingDeletionAsync", source, StringComparison.Ordinal);
        Assert.Contains("TransitionAfterAccountDeletionAsync", source, StringComparison.Ordinal);
        Assert.Equal(
            1,
            source.Split("ViewModel.DeleteAccountAsync", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void CredentialBuffersAreClearedInFinallyPaths()
    {
        var page = Read("src", "App", "Views", "SettingsPage.xaml.cs");
        var viewModel = Read("src", "App", "ViewModels", "SettingsViewModel.cs");

        Assert.Contains("Array.Clear(credentialCharacters)", page, StringComparison.Ordinal);
        Assert.Contains("credentialBox.Password = string.Empty", page, StringComparison.Ordinal);
        Assert.Contains("finally", viewModel, StringComparison.Ordinal);
        Assert.Contains("credential.Dispose()", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialCharacterCopyIsClearedBeforeAnyDeleteAwait()
    {
        var source = Read("src", "App", "Views", "SettingsPage.xaml.cs");
        var copy = source.IndexOf(
            "credentialBuffer = credentialBox.Password.ToCharArray()",
            StringComparison.Ordinal);
        var clear = source.IndexOf(
            "Array.Clear(credentialBuffer)",
            copy,
            StringComparison.Ordinal);
        var dispatch = source.IndexOf(
            "await ViewModel.DeleteAccountAsync",
            copy,
            StringComparison.Ordinal);

        Assert.True(copy >= 0);
        Assert.True(clear > copy);
        Assert.True(dispatch > clear);
        Assert.Contains("credentialCharacters = null;", source[clear..dispatch], StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyInjectionWiresDeletionFacadeIntoSettings()
    {
        var source = Read("src", "App", "Services", "ServiceConfiguration.cs");

        Assert.Contains(
            "AddSingleton<IAccountDeletionService, Vault.AccountDeletionService>",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "accountDeletion: sp.GetRequiredService<IAccountDeletionService>()",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LateDeletionResultUsesAtomicConditionalLifecycleRequest()
    {
        var source = Read("src", "App", "ViewModels", "SettingsViewModel.cs");
        var transitionStart = source.IndexOf(
            "TransitionAfterAccountDeletionAsync",
            StringComparison.Ordinal);
        var transitionEnd = source.IndexOf(
            "GetAccountDeletionRejectionMessage",
            transitionStart,
            StringComparison.Ordinal);
        var transition = source[transitionStart..transitionEnd];

        Assert.Contains("_lifecycle.RequestIfAsync", transition, StringComparison.Ordinal);
        Assert.Equal(
            1,
            transition.Split(
                "ShouldSkipAccountDeletionTransition(binding)",
                StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("_lifecycle.RequestAsync", transition, StringComparison.Ordinal);
        Assert.Contains("account.Endpoints != binding.Endpoints", transition, StringComparison.Ordinal);
        Assert.DoesNotContain("AppLifecycleIntent.Logout", transition, StringComparison.Ordinal);
    }

    [Fact]
    public void PageExitCancelsOnlyTheCurrentlyOwnedDeletionOperation()
    {
        var source = Read("src", "App", "Views", "SettingsPage.xaml.cs");
        var cancelStart = source.IndexOf(
            "private void CancelAccountDeletion()",
            StringComparison.Ordinal);
        var cancelEnd = source.IndexOf(
            "public void RefreshPolicyRestrictions",
            cancelStart,
            StringComparison.Ordinal);
        var cancel = source[cancelStart..cancelEnd];

        Assert.Contains("private CancellationTokenSource? _accountDeletionCts", source, StringComparison.Ordinal);
        Assert.Contains("protected override void OnNavigatedFrom", source, StringComparison.Ordinal);
        Assert.Contains("Unloaded += OnSettingsPageUnloaded", source, StringComparison.Ordinal);
        Assert.Contains("Volatile.Read(ref _accountDeletionCts)?.Cancel()", cancel, StringComparison.Ordinal);
        Assert.DoesNotContain(".Dispose(", cancel, StringComparison.Ordinal);
        var clearStart = source.LastIndexOf(
            "Interlocked.CompareExchange(",
            StringComparison.Ordinal);
        var clearEnd = source.IndexOf(");", clearStart, StringComparison.Ordinal);
        var clear = source[clearStart..(clearEnd + 2)];
        Assert.Contains("ref _accountDeletionCts", clear, StringComparison.Ordinal);
        Assert.Contains("value: null", clear, StringComparison.Ordinal);
        Assert.Contains("comparand: operationCts", clear, StringComparison.Ordinal);
    }

    private static string Read(params string[] relativeParts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, relativeParts));
    }
}
