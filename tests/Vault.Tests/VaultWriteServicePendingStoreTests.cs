using Core.Abstractions;
using Core.Models;
using Core.Services;
using Xunit;

namespace Vault.Tests;

public partial class VaultWriteServiceTests
{
    [Fact]
    public async Task SaveCipher_ReservationPersistenceFailure_BlocksPost()
    {
        var persistenceFailure = new IOException("ledger unavailable");
        _pendingStore.CreateFailure = persistenceFailure;
        var service = NewUnlockedService();

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None));

        Assert.Same(persistenceFailure, thrown);
        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
        Assert.Equal(1, _pendingStore.CreateCalls);
    }

    [Fact]
    public async Task SaveCipher_ConfirmedResponseUpgradesLedgerBeforeFailedReconciliation()
    {
        _api.CreateResponseId = "server-created-1";
        _sync.Failure = new IOException("sync unavailable");
        var service = NewUnlockedService();

        await Assert.ThrowsAsync<CipherCreateOutcomeUncertainException>(() =>
            service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None));

        var loaded = _pendingStore.Read(
            "test-account",
            ServerEndpoints.ForSelfHosted("https://vault.example"));
        Assert.Equal(CipherCreatePendingReadStatus.Found, loaded.Status);
        var pending = Assert.IsType<CipherCreatePending>(loaded.Pending);
        Assert.True(pending.CommitConfirmed);
        Assert.Equal("server-created-1", pending.ExpectedCipherId);
        Assert.True(_pendingStore.UpdateCalls > 0);
        Assert.Equal(0, _pendingStore.ConfirmedRemovalCalls);
    }

    [Fact]
    public async Task SaveCipher_UpgradeFailurePreservesAndHealsStrongestInMemoryState()
    {
        _api.CreateResponseId = "server-created-1";
        _pendingStore.UpdateFailure = new IOException("ledger update unavailable");
        var service = NewUnlockedService();

        var thrown = await Assert.ThrowsAsync<CipherCreateOutcomeUncertainException>(() =>
            service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None));
        Assert.True(thrown.Pending.CommitConfirmed);
        Assert.Equal("server-created-1", thrown.Pending.ExpectedCipherId);
        Assert.Equal(0, _sync.Calls);

        _pendingStore.UpdateFailure = null;
        var healed = Assert.IsType<CipherCreatePending>(service.GetPendingCipherCreate());
        Assert.True(healed.CommitConfirmed);
        Assert.Equal("server-created-1", healed.ExpectedCipherId);
        var durable = _pendingStore.Read(
            healed.Witness.AccountId,
            healed.Witness.Endpoints);
        Assert.True(Assert.IsType<CipherCreatePending>(durable.Pending).CommitConfirmed);
        Assert.Equal("server-created-1", durable.Pending!.ExpectedCipherId);
    }

    [Fact]
    public async Task SaveCipher_TwoHundredResponseWithEmptyIdStillPersistsCommitConfirmation()
    {
        _api.CreateResponseId = string.Empty;
        _sync.Failure = new IOException("sync unavailable");
        var service = NewUnlockedService();

        var thrown = await Assert.ThrowsAsync<CipherCreateOutcomeUncertainException>(() =>
            service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None));

        Assert.True(thrown.Pending.CommitConfirmed);
        Assert.Null(thrown.Pending.ExpectedCipherId);
        Assert.IsType<InvalidDataException>(thrown.MutationException);
        var durable = _pendingStore.Read(
            thrown.Pending.Witness.AccountId,
            thrown.Pending.Witness.Endpoints);
        Assert.True(Assert.IsType<CipherCreatePending>(durable.Pending).CommitConfirmed);
    }

    [Fact]
    public async Task SaveCipher_FinalConfirmationWriteFailureKeepsTerminalStateInMemory()
    {
        _api.CreateFailure = new HttpRequestException("response lost");
        _pendingStore.UpdateFailure = new IOException("terminal ledger update unavailable");
        var service = NewUnlockedService();

        await Assert.ThrowsAsync<IOException>(() =>
            service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None));

        _pendingStore.UpdateFailure = null;
        var healed = Assert.IsType<CipherCreatePending>(service.GetPendingCipherCreate());
        Assert.True(healed.CommitConfirmed);
        Assert.Equal("synced-created-1", healed.ExpectedCipherId);
    }

    [Fact]
    public async Task SaveCipher_ConfirmedRemovalFailureKeepsTerminalGateUntilRetrySucceeds()
    {
        _pendingStore.RemoveFailure = new IOException("ledger removal unavailable");
        var service = NewUnlockedService();

        await Assert.ThrowsAsync<IOException>(() =>
            service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None));

        var retained = Assert.IsType<CipherCreatePending>(service.GetPendingCipherCreate());
        Assert.True(retained.CommitConfirmed);
        Assert.Equal("synced-created-1", retained.ExpectedCipherId);
        Assert.True(service.HasPendingOrInFlightCipherCreate());

        _pendingStore.RemoveFailure = null;
        var result = await service.SyncAndReconcileCipherCreateAsync(
            retained,
            CancellationToken.None);

        Assert.Equal(CipherCreateReconciliationStatus.Confirmed, result.Status);
        Assert.False(service.HasPendingOrInFlightCipherCreate());
        Assert.Null(service.GetPendingCipherCreate());
    }

    [Fact]
    public async Task NewService_LazilyRecoversPendingReservationAfterRestart()
    {
        _api.CreateFailure = new HttpRequestException("response lost");
        _sync.Failure = new IOException("sync unavailable");
        var first = NewUnlockedService();
        var thrown = await Assert.ThrowsAsync<CipherCreateOutcomeUncertainException>(() =>
            first.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None));

        var restarted = NewUnlockedService();

        Assert.True(restarted.HasPendingOrInFlightCipherCreate());
        var recovered = Assert.IsType<CipherCreatePending>(restarted.GetPendingCipherCreate());
        Assert.Equal(
            thrown.Pending.Witness.KeyFingerprint,
            recovered.Witness.KeyFingerprint);
        await Assert.ThrowsAsync<CipherCreatePendingOperationException>(() =>
            restarted.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None));
        Assert.Equal("create", Assert.Single(_api.Calls));
    }

    [Fact]
    public async Task AbandonPendingCipherCreate_IsScopeBoundCasAndKeepsGateOnDeleteFailure()
    {
        _api.CreateFailure = new HttpRequestException("response lost");
        _sync.Failure = new IOException("sync unavailable");
        var (service, session) = NewUnlockedServiceWithSnapshot();
        await Assert.ThrowsAsync<CipherCreateOutcomeUncertainException>(() =>
            service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None));
        var pending = Assert.IsType<CipherCreatePending>(service.GetPendingCipherCreate());
        var conflicting = pending with
        {
            Witness = new CipherCreateWitness(
                pending.Witness.AccountId,
                pending.Witness.Endpoints,
                pending.Witness.CipherType,
                pending.Witness.OrganizationId,
                pending.Witness.CollectionIds,
                pending.Witness.BaselineCipherIds,
                new string('F', 64)),
        };

        Assert.False(service.AbandonPendingCipherCreate(conflicting));
        Assert.True(service.HasPendingOrInFlightCipherCreate());

        session.BeginAccountSession(
            "other-account",
            "other@example.com",
            ServerEndpoints.BitwardenEu);
        Assert.False(service.AbandonPendingCipherCreate(pending));
        session.BeginAccountSession(
            "test-account",
            "test@example.com",
            pending.Witness.Endpoints);

        _pendingStore.RemoveFailure = new IOException("delete unavailable");
        Assert.Throws<IOException>(() => service.AbandonPendingCipherCreate(pending));
        Assert.True(service.HasPendingOrInFlightCipherCreate());

        _pendingStore.RemoveFailure = null;
        Assert.True(service.AbandonPendingCipherCreate(pending));
        Assert.False(service.HasPendingOrInFlightCipherCreate());
        Assert.Null(service.GetPendingCipherCreate());
    }

    [Fact]
    public void StoreReadFailure_IsNotTreatedAsAbsent()
    {
        _pendingStore.ReadStatusOverride = CipherCreatePendingReadStatus.Corrupt;
        var service = NewUnlockedService();

        Assert.Null(service.GetPendingCipherCreate());
        Assert.True(service.HasPendingOrInFlightCipherCreate());
    }

    [Fact]
    public async Task PendingCreate_DoesNotBlockEditingAnExistingCipher()
    {
        var existing = NewLogin("cipher-1");
        var (service, session) = NewUnlockedServiceWithSnapshot(existing);
        var lease = session.CaptureLease();
        var pending = new CipherCreatePending(
            new CipherCreateWitness(
                lease.AccountId,
                lease.Endpoints,
                existing.Type,
                null,
                [],
                [existing.Id],
                new string('A', 64)),
            CommitConfirmed: false,
            ExpectedCipherId: null);
        _pendingStore.Seed(pending);

        await service.SaveCipherAsync(existing, CancellationToken.None);

        Assert.Equal("update", Assert.Single(_api.Calls));
        Assert.Equal(CipherCreatePendingReadStatus.Found, _pendingStore.Read(
            lease.AccountId,
            lease.Endpoints).Status);
    }
}
