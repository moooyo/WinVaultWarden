using App.Services;
using App.ViewModels;
using Core.Models;
using Core.Services;
using Core.Session;
using Xunit;

namespace App.Tests;

[Collection(TestCollectionNames.AppPreferences)]
public sealed class SettingsSecurityTransitionTests : AppPreferencesTestBase
{
    [Fact]
    public async Task Argon2Transition_PreservesAllParametersAndEndsTheCredentialSession()
    {
        var account = new AccountUi();
        var lifecycle = new Lifecycle();
        var model = new SettingsViewModel(account, lifecycle);
        var parameters = AccountKdfParameters.Argon2id(4, 96, 2);

        var result = await model.ChangeKdfAsync("current synthetic password", parameters);

        Assert.True(result.IsSuccess);
        Assert.Same(parameters, account.Parameters);
        Assert.Equal("current synthetic password", account.CurrentPassword);
        Assert.Equal([AppLifecycleIntent.CredentialChanged], lifecycle.Intents);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task RejectedKdfTransition_PreservesTheSessionAndReportsTheFailure()
    {
        var account = new AccountUi { Failure = new AccountOperationException("The server rejected the parameters.") };
        var lifecycle = new Lifecycle();
        var model = new SettingsViewModel(account, lifecycle);

        var result = await model.ChangeKdfAsync("synthetic password", AccountKdfParameters.Argon2id());

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal("The server rejected the parameters.", model.OperationError);
        Assert.Empty(lifecycle.Intents);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task BusySettings_DoNotDispatchAnotherKdfTransition()
    {
        var account = new AccountUi();
        var model = new SettingsViewModel(account) { IsBusy = true };

        var result = await model.ChangeKdfAsync("synthetic password", AccountKdfParameters.Argon2id());

        Assert.Equal(UiOperationStatus.Busy, result.Status);
        Assert.Null(account.Parameters);
    }

    [Fact]
    public void RestartPolicy_UpdatesTheProtectorServiceAndNotifiesItsBinding()
    {
        var pin = new Pin { IsPinSet = true };
        var model = new SettingsViewModel(pin);
        var changes = new List<string?>();
        model.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        model.SetPinRestartPolicy(true);

        Assert.True(pin.RequireMasterPasswordOnRestart);
        Assert.True(model.RequireMasterPasswordOnRestart);
        Assert.Contains(nameof(model.RequireMasterPasswordOnRestart), changes);
        model.ClearPin();
        Assert.False(model.RequireMasterPasswordOnRestart);
    }

    [Fact]
    public void RestartPolicy_CannotBeChangedWithoutAnEligiblePin()
    {
        var pin = new Pin();
        var model = new SettingsViewModel(pin);

        Assert.Throws<InvalidOperationException>(() => model.SetPinRestartPolicy(true));
        Assert.False(pin.RequireMasterPasswordOnRestart);
    }

    private sealed class AccountUi : IAccountUiService
    {
        public AccountKdfParameters? Parameters { get; private set; }
        public string? CurrentPassword { get; private set; }
        public Exception? Failure { get; init; }
        public AccountInfo GetAccount() => AccountInfo.Empty;
        public Task RenameAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task ChangePasswordAsync(string current, string next, string confirm, string? hint, CancellationToken ct = default) => Task.CompletedTask;
        public Task ChangeIterationsAsync(string current, int iterations, CancellationToken ct = default) =>
            throw new InvalidOperationException("The explicit KDF operation must not be coerced to PBKDF2.");
        public Task ChangeKdfAsync(string current, AccountKdfParameters parameters, CancellationToken ct = default)
        {
            CurrentPassword = current;
            Parameters = parameters;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class Pin : IPinService
    {
        public bool IsPinSet { get; set; }
        public bool RequireMasterPasswordOnRestart { get; private set; }
        public void SetPin(string pin) => IsPinSet = true;
        public void SetRequireMasterPasswordOnRestart(bool value) => RequireMasterPasswordOnRestart = value;
        public void ClearPin()
        {
            IsPinSet = false;
            RequireMasterPasswordOnRestart = false;
        }
    }

    private sealed class Lifecycle : IAppLifecycleCoordinator
    {
        public List<AppLifecycleIntent> Intents { get; } = [];
        public bool IsTransitioning => false;
        public Task RequestAsync(AppLifecycleIntent intent, VaultSessionLease? expectedSession = null)
        {
            Intents.Add(intent);
            return Task.CompletedTask;
        }
        public Task<bool> RequestIfAsync(AppLifecycleIntent intent, Func<bool> predicate) =>
            Task.FromResult(predicate());
        public bool MarkSessionReady() => true;
        public void FailClosedSynchronously() { }
    }
}
