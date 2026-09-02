using App.Services;
using Core.Abstractions;
using Core.Models;
using Core.Services;
using Xunit;

namespace App.Tests;

public sealed class AccountDeletionLocalWriteGuardTests
{
    [Fact]
    public void TryAcquire_AbsentLedgerReturnsLeaseAndHoldsCoordinationUntilDisposed()
    {
        var gate = new TrackingCoordinationGate();
        var tombstones = new StubTombstoneStore(AccountDeletionTombstoneReadAllResult.Absent);
        var guard = new AccountDeletionLocalWriteGuard(gate, tombstones);

        var lease = guard.TryAcquire("account-a", Endpoints("https://a.example"));

        Assert.NotNull(lease);
        Assert.Equal(1, gate.TryAcquireCalls);
        Assert.Equal(1, tombstones.ReadAllCalls);
        Assert.Equal(0, gate.DisposeCalls);

        lease.Dispose();
        Assert.Equal(1, gate.DisposeCalls);
    }

    [Theory]
    [InlineData(AccountDeletionStage.Prepared)]
    [InlineData(AccountDeletionStage.ServerConfirmed)]
    [InlineData(AccountDeletionStage.LocalCleanupComplete)]
    [InlineData(AccountDeletionStage.UncertainLocalCleanupComplete)]
    public void TryAcquire_MatchingDeletionScopeIsBlockedAtEveryStage(
        AccountDeletionStage stage)
    {
        var endpoints = Endpoints("https://vault.example");
        var record = Record("account-a", endpoints, stage);
        var gate = new TrackingCoordinationGate();
        var guard = new AccountDeletionLocalWriteGuard(
            gate,
            new StubTombstoneStore(AccountDeletionTombstoneReadAllResult.Found([record])));

        var lease = guard.TryAcquire("account-a", endpoints);

        Assert.Null(lease);
        Assert.Equal(1, gate.DisposeCalls);
    }

    [Fact]
    public void TryAcquire_FoundLedgerWithoutMatchingCompleteScopeIsAllowed()
    {
        var targetEndpoints = Endpoints("https://vault.example");
        var records = new[]
        {
            Record("account-b", targetEndpoints, AccountDeletionStage.LocalCleanupComplete),
            Record(
                "account-a",
                Endpoints("https://other.example"),
                AccountDeletionStage.LocalCleanupComplete),
        };
        var gate = new TrackingCoordinationGate();
        var guard = new AccountDeletionLocalWriteGuard(
            gate,
            new StubTombstoneStore(AccountDeletionTombstoneReadAllResult.Found(records)));

        using var lease = guard.TryAcquire("account-a", targetEndpoints);

        Assert.NotNull(lease);
    }

    [Theory]
    [InlineData(AccountDeletionTombstoneReadStatus.Corrupt)]
    [InlineData(AccountDeletionTombstoneReadStatus.Unavailable)]
    public void TryAcquire_UnreadableDeletionStateFailsClosed(
        AccountDeletionTombstoneReadStatus status)
    {
        var read = status == AccountDeletionTombstoneReadStatus.Corrupt
            ? AccountDeletionTombstoneReadAllResult.Corrupt
            : AccountDeletionTombstoneReadAllResult.Unavailable;
        var gate = new TrackingCoordinationGate();
        var guard = new AccountDeletionLocalWriteGuard(gate, new StubTombstoneStore(read));

        var lease = guard.TryAcquire("account-a", Endpoints("https://vault.example"));

        Assert.Null(lease);
        Assert.Equal(1, gate.DisposeCalls);
    }

    [Fact]
    public void TryAcquire_CoordinationContentionDoesNotReadLedger()
    {
        var gate = new TrackingCoordinationGate { Contended = true };
        var tombstones = new StubTombstoneStore(AccountDeletionTombstoneReadAllResult.Absent);
        var guard = new AccountDeletionLocalWriteGuard(gate, tombstones);

        var lease = guard.TryAcquire("account-a", Endpoints("https://vault.example"));

        Assert.Null(lease);
        Assert.Equal(0, tombstones.ReadAllCalls);
        Assert.Equal(0, gate.DisposeCalls);
    }

    [Fact]
    public void TryAcquire_CoordinationOrLedgerFailureFailsClosedAndReleasesAcquiredGate()
    {
        var acquisitionFailure = new AccountDeletionLocalWriteGuard(
            new TrackingCoordinationGate { Failure = new UnauthorizedAccessException() },
            new StubTombstoneStore(AccountDeletionTombstoneReadAllResult.Absent));
        Assert.Null(acquisitionFailure.TryAcquire(
            "account-a",
            Endpoints("https://vault.example")));

        var gate = new TrackingCoordinationGate();
        var readFailure = new AccountDeletionLocalWriteGuard(
            gate,
            new StubTombstoneStore(
                AccountDeletionTombstoneReadAllResult.Absent,
                new IOException("ledger unavailable")));

        Assert.Null(readFailure.TryAcquire(
            "account-a",
            Endpoints("https://vault.example")));
        Assert.Equal(1, gate.DisposeCalls);
    }

    [Fact]
    public void AcquireOrThrow_UsesExplicitBlockedExceptionForNullOrThrownGuard()
    {
        var endpoints = Endpoints("https://vault.example");
        Assert.Throws<AccountDeletionWriteBlockedException>(() =>
            new StubWriteGuard().AcquireOrThrow("account-a", endpoints));

        var failure = Assert.Throws<AccountDeletionWriteBlockedException>(() =>
            new StubWriteGuard(new IOException("gate failed"))
                .AcquireOrThrow("account-a", endpoints));
        Assert.IsType<IOException>(failure.InnerException);
    }

    [Fact]
    public void ProductionSource_RegistersRealGuardAndExplicitlyInjectsEveryProtectedStore()
    {
        var services = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "App",
            "Services",
            "ServiceConfiguration.cs"));

        Assert.Contains(
            "AddSingleton<IAccountDeletionLocalWriteGuard,",
            services,
            StringComparison.Ordinal);
        Assert.Contains("AccountDeletionLocalWriteGuard>();", services, StringComparison.Ordinal);
        foreach (var store in new[]
        {
            "DpapiTokenStore",
            "DpapiVaultCacheStore",
            "DpapiDeviceKeyStore",
            "DpapiCipherCreatePendingStore",
            "DpapiTrustedDeviceApprovalStore",
            "DpapiTdeV2JitPendingStore",
        })
        {
            Assert.Contains($"new {store}(", services, StringComparison.Ordinal);
        }

        Assert.Equal(
            6,
            services.Split(
                "sp.GetRequiredService<IAccountDeletionLocalWriteGuard>()",
                StringSplitOptions.None).Length - 1);
    }

    private static AccountDeletionTombstoneRecord Record(
        string accountId,
        ServerEndpoints endpoints,
        AccountDeletionStage stage)
    {
        var created = DateTimeOffset.UnixEpoch.AddDays(1);
        var updated = stage == AccountDeletionStage.Prepared
            ? created
            : created.AddMinutes(1);
        return new AccountDeletionTombstoneRecord(
            Guid.NewGuid(),
            revision: stage == AccountDeletionStage.Prepared ? 1 : 2,
            created,
            updated,
            new AccountDeletionBinding(
                accountId,
                $"{accountId}@example.com",
                endpoints,
                Guid.NewGuid().ToString("D")),
            stage);
    }

    private static ServerEndpoints Endpoints(string webVault) =>
        ServerEndpoints.ForSelfHosted(webVault);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinVaultWarden.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("The repository root could not be located.");
    }

    private sealed class TrackingCoordinationGate : IAccountDeletionCoordinationGate
    {
        public bool Contended { get; init; }
        public Exception? Failure { get; init; }
        public int TryAcquireCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public IAccountDeletionCoordinationLease? TryAcquire()
        {
            TryAcquireCalls++;
            if (Failure is not null)
                throw Failure;
            return Contended ? null : new Lease(this);
        }

        public ValueTask<IAccountDeletionCoordinationLease> AcquireAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private sealed class Lease(TrackingCoordinationGate owner)
            : IAccountDeletionCoordinationLease
        {
            private TrackingCoordinationGate? _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is not null)
                    owner.DisposeCalls++;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class StubTombstoneStore(
        AccountDeletionTombstoneReadAllResult readAll,
        Exception? failure = null) : IAccountDeletionTombstoneStore
    {
        public int ReadAllCalls { get; private set; }

        public AccountDeletionTombstoneReadAllResult ReadAll()
        {
            ReadAllCalls++;
            if (failure is not null)
                throw failure;
            return readAll;
        }

        public AccountDeletionTombstoneReadResult Read(AccountDeletionBinding binding) =>
            throw new NotSupportedException();

        public AccountDeletionTombstoneRecord? TryCreate(
            AccountDeletionBinding binding,
            DateTimeOffset createdAt) => throw new NotSupportedException();

        public AccountDeletionTombstoneRecord? TryAdvance(
            AccountDeletionTombstoneRecord expected,
            AccountDeletionStage nextStage,
            DateTimeOffset updatedAt) => throw new NotSupportedException();

        public bool Remove(
            AccountDeletionTombstoneRecord expected,
            AccountDeletionTombstoneRemovalReason reason) => throw new NotSupportedException();
    }

    private sealed class StubWriteGuard(Exception? failure = null)
        : IAccountDeletionLocalWriteGuard
    {
        public IDisposable? TryAcquire(string accountId, ServerEndpoints endpoints)
        {
            if (failure is not null)
                throw failure;
            return null;
        }
    }
}
