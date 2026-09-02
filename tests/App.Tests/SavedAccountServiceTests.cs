using App.Services;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Vault;
using Xunit;

namespace App.Tests;

public sealed class SavedAccountServiceTests
{
    [Fact]
    public void AccountsExposeOnlyDisplayMetadataAndActivationChangesActiveSession()
    {
        var store = new MemoryTokenStore();
        var first = Session("account-a", "https://a.example");
        var second = Session("account-b", "https://b.example");
        store.Save(first);
        store.Save(second);
        var service = new SavedAccountService(store);

        var accounts = service.GetAccounts();

        Assert.Equal([second.AccountId, first.AccountId], accounts.Select(account => account.AccountId));
        Assert.True(accounts[0].IsActive);
        Assert.False(accounts[1].IsActive);
        Assert.Equal("A", accounts[1].Initial);
        Assert.Equal("https://a.example", accounts[1].ServerUrl);

        Assert.True(service.TryActivate(accounts[1]));
        Assert.True(store.TryLoad(out var active));
        Assert.Equal(first.AccountId, active.AccountId);
        Assert.Equal([first.AccountId, second.AccountId],
            service.GetAccounts().Select(account => account.AccountId));
    }

    [Theory]
    [InlineData(AccountDeletionStage.Prepared)]
    [InlineData(AccountDeletionStage.ServerConfirmed)]
    [InlineData(AccountDeletionStage.LocalCleanupComplete)]
    [InlineData(AccountDeletionStage.UncertainLocalCleanupComplete)]
    public void TombstonedBinding_IsHiddenAndCannotBeActivated(
        AccountDeletionStage stage)
    {
        var store = new MemoryTokenStore();
        var hidden = Session("account-a", "https://a.example");
        var visible = Session("account-b", "https://b.example");
        store.Save(hidden);
        store.Save(visible);
        var pending = Pending(hidden, stage);
        var deletion = new ControlledAccountDeletionService
        {
            ReadAllResult = AccountDeletionTombstoneReadAllResult.Found([pending]),
        };
        var service = new SavedAccountService(store, deletion);

        var accounts = service.GetAccounts();

        var remaining = Assert.Single(accounts);
        Assert.Equal(visible.AccountId, remaining.AccountId);
        Assert.False(service.TryActivate(new SavedAccountOption(
            hidden.AccountId,
            hidden.Email,
            hidden.Endpoints!,
            IsActive: false)));
    }

    [Theory]
    [InlineData(AccountDeletionTombstoneReadStatus.Corrupt)]
    [InlineData(AccountDeletionTombstoneReadStatus.Unavailable)]
    public void UnsafeLedgerState_HidesAndRejectsEverySavedAccount(
        AccountDeletionTombstoneReadStatus status)
    {
        var store = new MemoryTokenStore();
        var session = Session("account-a", "https://a.example");
        store.Save(session);
        var deletion = new ControlledAccountDeletionService
        {
            ReadAllResult = status == AccountDeletionTombstoneReadStatus.Corrupt
                ? AccountDeletionTombstoneReadAllResult.Corrupt
                : AccountDeletionTombstoneReadAllResult.Unavailable,
        };
        var service = new SavedAccountService(store, deletion);
        var option = new SavedAccountOption(
            session.AccountId,
            session.Email,
            session.Endpoints!,
            IsActive: true);

        Assert.Empty(service.GetAccounts());
        Assert.False(service.TryActivate(option));
    }

    [Fact]
    public void LedgerReadException_FailsClosed()
    {
        var store = new MemoryTokenStore();
        var session = Session("account-a", "https://a.example");
        store.Save(session);
        var service = new SavedAccountService(
            store,
            new ControlledAccountDeletionService { ThrowOnRead = true });

        Assert.Empty(service.GetAccounts());
        Assert.False(service.TryActivate(new SavedAccountOption(
            session.AccountId,
            session.Email,
            session.Endpoints!,
            IsActive: true)));
    }

    private static PersistedSession Session(string accountId, string serverUrl) => new(
        PersistedSession.CurrentSchemaVersion,
        accountId,
        ServerEndpoints.FromWebVault(serverUrl),
        7,
        $"{accountId}@example.com",
        Guid.NewGuid().ToString("D"),
        $"refresh-{accountId}",
        $"key-{accountId}",
        KdfType.Pbkdf2,
        600_000,
        null,
        null);

    private static AccountDeletionTombstoneRecord Pending(
        PersistedSession session,
        AccountDeletionStage stage)
    {
        var now = DateTimeOffset.UtcNow;
        return new AccountDeletionTombstoneRecord(
            Guid.NewGuid(),
            stage switch
            {
                AccountDeletionStage.Prepared => 1,
                AccountDeletionStage.ServerConfirmed => 2,
                AccountDeletionStage.LocalCleanupComplete => 3,
                AccountDeletionStage.UncertainLocalCleanupComplete => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(stage)),
            },
            now,
            stage == AccountDeletionStage.Prepared
                ? now
                : now.AddSeconds(1),
            new AccountDeletionBinding(
                session.AccountId,
                session.Email,
                session.Endpoints!,
                session.DeviceIdentifier),
            stage);
    }
}
