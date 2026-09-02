using Api;
using Api.Dtos;
using System.Net;
using System.Security.Cryptography;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public partial class VaultWriteServiceTests
{
    private sealed class FakeWriteApi : IVaultWriteApiClient
    {
        public List<string> Calls { get; } = new();
        public string? LastId { get; private set; }
        public CipherRequest? LastCipher { get; private set; }
        public ShareCipherRequest? LastSharedCipher { get; private set; }
        public PartialCipherRequest? LastPartialCipher { get; private set; }
        public FolderRequest? LastFolder { get; private set; }
        public IReadOnlyCollection<string>? LastIds { get; private set; }
        public string? LastFolderId { get; private set; }
        public Exception? BulkFailure { get; set; }
        public Exception? CreateFailure { get; set; }
        public string? CreateResponseId { get; set; }
        public TaskCompletionSource? CreateEntered { get; set; }
        public TaskCompletionSource? CreateRelease { get; set; }

        public Task CreateCipherAsync(CipherRequest request, CancellationToken ct = default)
        {
            Calls.Add("create");
            LastCipher = request;
            return CreateResult();
        }

        public async Task<CipherDto?> CreateCipherWithResponseAsync(
            CipherRequest request,
            CancellationToken ct = default)
        {
            await CreateCipherAsync(request, ct);
            return CreateResponse();
        }

        public Task CreateOrganizationCipherAsync(ShareCipherRequest request, CancellationToken ct = default)
        {
            Calls.Add("org-create");
            LastSharedCipher = request;
            LastCipher = request.Cipher;
            return CreateResult();
        }

        public async Task<CipherDto?> CreateOrganizationCipherWithResponseAsync(
            ShareCipherRequest request,
            CancellationToken ct = default)
        {
            await CreateOrganizationCipherAsync(request, ct);
            return CreateResponse();
        }

        public Task UpdateCipherAsync(string cipherId, CipherRequest request, CancellationToken ct = default)
        { Calls.Add("update"); LastId = cipherId; LastCipher = request; return Task.CompletedTask; }

        public Task UpdateCipherPartialAsync(
            string cipherId,
            PartialCipherRequest request,
            CancellationToken ct = default)
        { Calls.Add("partial"); LastId = cipherId; LastPartialCipher = request; return Task.CompletedTask; }

        public Task SoftDeleteCipherAsync(string cipherId, CancellationToken ct = default)
        { Calls.Add("soft"); LastId = cipherId; return Task.CompletedTask; }

        public Task HardDeleteCipherAsync(string cipherId, CancellationToken ct = default)
        { Calls.Add("hard"); LastId = cipherId; return Task.CompletedTask; }

        public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default)
        { Calls.Add("restore"); LastId = cipherId; return Task.CompletedTask; }

        public Task ArchiveCipherAsync(string cipherId, CancellationToken ct = default)
        { Calls.Add("archive"); LastId = cipherId; return Task.CompletedTask; }

        public Task UnarchiveCipherAsync(string cipherId, CancellationToken ct = default)
        { Calls.Add("unarchive"); LastId = cipherId; return Task.CompletedTask; }

        public Task BulkSoftDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
        { Calls.Add("bulk-soft"); LastIds = ids; return BulkResult(); }

        public Task BulkHardDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
        { Calls.Add("bulk-hard"); LastIds = ids; return BulkResult(); }

        public Task BulkRestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
        { Calls.Add("bulk-restore"); LastIds = ids; return BulkResult(); }

        public Task BulkArchiveCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
        { Calls.Add("bulk-archive"); LastIds = ids; return BulkResult(); }

        public Task BulkUnarchiveCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
        { Calls.Add("bulk-unarchive"); LastIds = ids; return BulkResult(); }

        public Task BulkMoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default)
        { Calls.Add("bulk-move"); LastIds = ids; LastFolderId = folderId; return BulkResult(); }

        public Task CreateFolderAsync(FolderRequest request, CancellationToken ct = default)
        { Calls.Add("folder-create"); LastFolder = request; return Task.CompletedTask; }

        public Task UpdateFolderAsync(string folderId, FolderRequest request, CancellationToken ct = default)
        { Calls.Add("folder-update"); LastId = folderId; LastFolder = request; return Task.CompletedTask; }

        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default)
        { Calls.Add("folder-delete"); LastId = folderId; return Task.CompletedTask; }

        public Task ImportCiphersAsync(ImportRequest request, CancellationToken ct = default)
        { Calls.Add("import"); return Task.CompletedTask; }

        private Task BulkResult() => BulkFailure is null
            ? Task.CompletedTask
            : Task.FromException(BulkFailure);

        private async Task CreateResult()
        {
            CreateEntered?.TrySetResult();
            if (CreateRelease is not null)
                await CreateRelease.Task;
            if (CreateFailure is not null)
                throw CreateFailure;
        }

        private CipherDto? CreateResponse() => CreateResponseId is null
            ? null
            : new CipherDto(
                CreateResponseId,
                1,
                "2.name",
                null,
                null,
                null,
                null,
                false,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
    }

    private sealed class FakeSync : ISyncService
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        public Action? BeforeCompletion { get; set; }
        public Func<IReadOnlyList<Cipher>>? ResultFactory { get; set; }
        public VaultSessionLease? LastLease { get; private set; }
        public bool LastTokenWasCancellationRequested { get; private set; }

        public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default)
        {
            Calls++;
            LastTokenWasCancellationRequested = ct.IsCancellationRequested;
            BeforeCompletion?.Invoke();
            if (Failure is not null)
                return Task.FromException<IReadOnlyList<Cipher>>(Failure);
            return Task.FromResult(ResultFactory?.Invoke() ?? (IReadOnlyList<Cipher>)Array.Empty<Cipher>());
        }

        public Task<IReadOnlyList<Cipher>> SyncAsync(
            Core.Session.VaultSessionLease lease, CancellationToken ct = default)
        {
            LastLease = lease;
            return SyncAsync(ct);
        }
    }

    private readonly FakeWriteApi _api = new();
    private readonly FakeSync _sync = new();
    private readonly FakeCipherCreatePendingStore _pendingStore = new();
    private static readonly VaultCapabilities NewCipherTypesEnabled = new(true, false, false);
    private static readonly VaultCapabilities ArchiveEnabled = new(false, true, true);
    private static readonly VaultCapabilities ArchiveProtocolOnly = new(false, true, false);

    public VaultWriteServiceTests()
    {
        _sync.ResultFactory = DefaultSyncResult;
    }

    private IReadOnlyList<Cipher> DefaultSyncResult()
    {
        if (_api.LastCipher?.Key is null)
            return Array.Empty<Cipher>();

        return
        [
            CreatedCipherFromLastRequest(
                string.IsNullOrWhiteSpace(_api.CreateResponseId)
                    ? "synced-created-1"
                    : _api.CreateResponseId),
        ];
    }

    private Cipher CreatedCipherFromLastRequest(string id) => new()
    {
        Id = id,
        Type = (CipherType)_api.LastCipher!.Type,
        OrganizationId = _api.LastCipher.OrganizationId,
        CollectionIds = _api.LastSharedCipher?.CollectionIds ?? Array.Empty<string>(),
        Key = _api.LastCipher.Key,
        Name = "Synced created cipher",
    };

    private VaultWriteService NewUnlockedService(VaultCapabilities? capabilities = null)
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession("test-account", "test@test.com", new Core.Models.ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        Assert.True(session.TrySetCapabilities(lease, capabilities ?? VaultCapabilities.None));
        return new VaultWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api),
            new CipherEncryptor(new CryptoService()),
            _sync,
            session,
            cipherCreateStore: _pendingStore);
    }

    private (VaultWriteService Service, VaultSession Session) NewUnlockedServiceWithSnapshot(params Cipher[] ciphers)
        => NewUnlockedServiceWithSnapshot(VaultCapabilities.None, ciphers);

    private (VaultWriteService Service, VaultSession Session) NewUnlockedServiceWithSnapshot(
        VaultCapabilities capabilities,
        params Cipher[] ciphers)
    {
        var session = new VaultSession();
        var account = new AccountInfo(
            "test-account",
            "test@test.com",
            new Core.Models.ServerOrigin("https://vault.example"),
            "T",
            string.Empty);
        session.BeginAccountSession(account.Id, account.Email, account.Endpoints!);
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        session.SetSnapshot(
            new DecryptedVault(account, [], ciphers, 0, 0),
            capabilities: capabilities);
        var service = new VaultWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api),
            new CipherEncryptor(new CryptoService()),
            _sync,
            session,
            cipherCreateStore: _pendingStore);
        return (service, session);
    }

    private static Cipher NewLogin(
        string id = "",
        string? organizationId = null,
        string? folderId = null,
        bool? canEdit = null,
        bool? canDelete = null,
        bool? canRestore = null) => new()
    {
        Id = id,
        OrganizationId = organizationId,
        FolderId = folderId,
        Type = CipherType.Login,
        Name = "Item",
        CanEdit = canEdit ?? organizationId is null,
        CanDelete = canDelete ?? organizationId is null,
        CanRestore = canRestore ?? organizationId is null,
        Login = new CipherLogin("user", "pw", null, Array.Empty<CipherLoginUri>()),
    };

    private static Cipher NewCipherType(CipherType type) => type switch
    {
        CipherType.BankAccount => new Cipher
        {
            Type = type,
            Name = "Bank",
            BankAccount = new CipherBankAccount(
                "Example", "Ada", "Checking", "123", "456", "001", "7890", "SWIFT", "IBAN", "555"),
        },
        CipherType.DriversLicense => new Cipher
        {
            Type = type,
            Name = "License",
            DriversLicense = new CipherDriversLicense(
                "Ada", "M", "Lovelace", "1815", "DL-1", "GB", "London", "2025", "2030", "DVLA", "B"),
        },
        CipherType.Passport => new Cipher
        {
            Type = type,
            Name = "Passport",
            Passport = new CipherPassport(
                "Lovelace", "Ada", "1815", "F", "London", "British", "GB", "P-1", "P", "N-1", "HMPO", "2025", "2035"),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    [Fact]
    public async Task SaveCipher_NewItem_CreatesThenResyncs()
    {
        await NewUnlockedService().SaveCipherAsync(NewLogin(), TestContext.Current.CancellationToken);

        Assert.Equal("create", Assert.Single(_api.Calls));
        Assert.False(string.IsNullOrEmpty(_api.LastCipher!.Key));
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipherAndGetId_NewItem_ReturnsAuthoritativeApiId()
    {
        _api.CreateResponseId = "server-created-1";

        var id = await NewUnlockedService().SaveCipherAndGetIdAsync(
            NewLogin(),
            TestContext.Current.CancellationToken);

        Assert.Equal("server-created-1", id);
        Assert.Equal("create", Assert.Single(_api.Calls));
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_CreateInFlight_RejectsLeaseBoundSecondCreateBeforeApi()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.CreateEntered = entered;
        _api.CreateRelease = release;
        var (service, session) = NewUnlockedServiceWithSnapshot();

        var first = service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(service.HasPendingOrInFlightCipherCreate());
        Assert.Null(service.GetPendingCipherCreate());
        await Assert.ThrowsAsync<CipherCreatePendingOperationException>(() =>
            service.SaveCipherAsync(
                NewLogin(),
                session.CaptureLease(),
                CancellationToken.None));
        Assert.Equal("create", Assert.Single(_api.Calls));

        release.TrySetResult();
        Assert.Equal("synced-created-1", await first);
        Assert.False(service.HasPendingOrInFlightCipherCreate());
        Assert.Null(service.GetPendingCipherCreate());
    }

    [Fact]
    public async Task ReconcileCipherCreate_WhileSameCreateIsInFlight_DoesNotClearReservation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.CreateEntered = entered;
        _api.CreateRelease = release;
        var (service, session) = NewUnlockedServiceWithSnapshot();

        var first = service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var wrappedKey = Assert.IsType<string>(_api.LastCipher!.Key);
        var stalePending = new CipherCreatePending(
            new CipherCreateWitness(
                "test-account",
                ServerEndpoints.ForSelfHosted("https://vault.example"),
                CipherType.Login,
                null,
                [],
                [],
                CipherCreateWitness.ComputeKeyFingerprint(wrappedKey)),
            CommitConfirmed: false,
            ExpectedCipherId: null);

        await Assert.ThrowsAsync<CipherCreatePendingOperationException>(() =>
            service.SyncAndReconcileCipherCreateAsync(stalePending, CancellationToken.None));
        Assert.True(service.HasPendingOrInFlightCipherCreate());
        Assert.Null(service.GetPendingCipherCreate());
        Assert.Equal(0, _sync.Calls);
        await Assert.ThrowsAsync<CipherCreatePendingOperationException>(() =>
            service.SaveCipherAsync(NewLogin(), session.CaptureLease(), CancellationToken.None));
        Assert.Equal("create", Assert.Single(_api.Calls));

        release.TrySetResult();
        Assert.Equal("synced-created-1", await first);
    }

    [Fact]
    public async Task SaveCipher_CreateFailure_RetainsPendingWithoutGateReleaseWindow()
    {
        _api.CreateFailure = new HttpRequestException("response lost");
        _sync.Failure = new IOException("sync failed");
        var (service, session) = NewUnlockedServiceWithSnapshot();
        var observedStates = new List<(bool HasGate, bool HasPending)>();
        service.CipherCreateGateChanged += (_, _) => observedStates.Add((
            service.HasPendingOrInFlightCipherCreate(),
            service.GetPendingCipherCreate() is not null));

        var thrown = await Assert.ThrowsAsync<CipherCreateOutcomeUncertainException>(() =>
            service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None));

        Assert.True(service.HasPendingOrInFlightCipherCreate());
        var retained = Assert.IsType<CipherCreatePending>(service.GetPendingCipherCreate());
        Assert.Equal(thrown.Pending.Witness.KeyFingerprint, retained.Witness.KeyFingerprint);
        Assert.NotEmpty(observedStates);
        Assert.All(observedStates, state => Assert.True(state.HasGate));
        Assert.Contains(observedStates, state => !state.HasPending);
        Assert.Contains(observedStates, state => state.HasPending);

        await Assert.ThrowsAsync<CipherCreatePendingOperationException>(() =>
            service.SaveCipherAsync(
                NewLogin(),
                session.CaptureLease(),
                CancellationToken.None));
        Assert.Equal("create", Assert.Single(_api.Calls));
    }

    [Fact]
    public async Task SaveCipher_PendingGate_IsScopedByAccountAndFullEndpoints()
    {
        _api.CreateFailure = new HttpRequestException("response lost");
        _sync.Failure = new IOException("sync failed");
        var (service, session) = NewUnlockedServiceWithSnapshot();

        await Assert.ThrowsAsync<CipherCreateOutcomeUncertainException>(() =>
            service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None));
        Assert.True(service.HasPendingOrInFlightCipherCreate());

        var originalEndpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        var alternateApiEndpoints = new ServerEndpoints(
            originalEndpoints.WebVault,
            new ServerEndpoint("https://alternate-api.example"),
            originalEndpoints.Identity,
            originalEndpoints.Notifications,
            originalEndpoints.Icons,
            originalEndpoints.SendWeb);
        session.BeginAccountSession(
            "test-account",
            "test@test.com",
            alternateApiEndpoints);
        Assert.False(service.HasPendingOrInFlightCipherCreate());

        session.BeginAccountSession(
            "other-account",
            "other@example.com",
            ServerEndpoints.BitwardenEu);
        Assert.False(service.HasPendingOrInFlightCipherCreate());
        Assert.Null(service.GetPendingCipherCreate());

        session.BeginAccountSession(
            "test-account",
            "test@test.com",
            originalEndpoints);
        Assert.True(service.HasPendingOrInFlightCipherCreate());
        Assert.NotNull(service.GetPendingCipherCreate());
    }

    [Fact]
    public async Task SaveCipher_GateObserverFailure_DoesNotLeakReservationOrReplaceSuccess()
    {
        var service = NewUnlockedService();
        service.CipherCreateGateChanged += (_, _) => throw new InvalidOperationException("observer failed");

        var id = await service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None);

        Assert.Equal("synced-created-1", id);
        Assert.False(service.HasPendingOrInFlightCipherCreate());
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("json")]
    [InlineData("invalid-data")]
    public async Task SaveCipherAndGetId_CreateNon4xxFailure_WithUniqueWitnessReturnsSyncedId(
        string failureKind)
    {
        Exception failure = failureKind switch
        {
            "http" => new HttpRequestException("response lost"),
            "timeout" => new TimeoutException("response timed out"),
            "cancel" => new OperationCanceledException("response read was cancelled"),
            "json" => new System.Text.Json.JsonException("response JSON was invalid"),
            "invalid-data" => new InvalidDataException("response was invalid"),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind)),
        };
        _api.CreateFailure = failure;

        var id = await NewUnlockedService().SaveCipherAndGetIdAsync(
            NewLogin(),
            TestContext.Current.CancellationToken);

        Assert.Equal("synced-created-1", id);
        Assert.Equal("create", Assert.Single(_api.Calls));
        Assert.Equal(1, _sync.Calls);
        Assert.False(_sync.LastTokenWasCancellationRequested);
    }

    [Fact]
    public async Task SaveCipherAndGetId_CreateServerFailure_WithUniqueWitnessReturnsSyncedId()
    {
        _api.CreateFailure = new VaultWriteException(
            HttpStatusCode.InternalServerError,
            "server failed after processing the request",
            validationErrors: null);

        var id = await NewUnlockedService().SaveCipherAndGetIdAsync(
            NewLogin(),
            TestContext.Current.CancellationToken);

        Assert.Equal("synced-created-1", id);
        Assert.Equal(1, _sync.Calls);
        Assert.False(_sync.LastTokenWasCancellationRequested);
    }

    [Fact]
    public async Task SaveCipherAndGetId_NullSuccessDto_ReturnsIdFromUniqueWitness()
    {
        Assert.Null(_api.CreateResponseId);

        var id = await NewUnlockedService().SaveCipherAndGetIdAsync(
            NewLogin(),
            TestContext.Current.CancellationToken);

        Assert.Equal("synced-created-1", id);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipherAndGetId_CreateTransportFailure_WithoutWitnessRemainsPending()
    {
        var failure = new HttpRequestException("response lost");
        _api.CreateFailure = failure;
        _sync.ResultFactory = () => Array.Empty<Cipher>();

        var thrown = await Assert.ThrowsAsync<CipherCreateSnapshotMismatchException>(() =>
            NewUnlockedService().SaveCipherAndGetIdAsync(
                NewLogin(),
                TestContext.Current.CancellationToken));

        Assert.False(thrown.Pending.CommitConfirmed);
        Assert.Equal(CipherCreateReconciliationStatus.NotFound, thrown.Reconciliation.Status);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipherAndGetId_SuccessResponseReadFailure_WithoutWitnessStaysCommitConfirmed()
    {
        _api.CreateFailure = new CipherCreateSuccessResponseReadException(
            HttpStatusCode.OK,
            new System.Text.Json.JsonException("truncated response"));
        _sync.ResultFactory = () => Array.Empty<Cipher>();

        var thrown = await Assert.ThrowsAsync<CipherCreateSnapshotMismatchException>(() =>
            NewUnlockedService().SaveCipherAndGetIdAsync(
                NewLogin(),
                TestContext.Current.CancellationToken));

        Assert.True(thrown.Pending.CommitConfirmed);
        Assert.Equal(CipherCreateReconciliationStatus.NotFound, thrown.Reconciliation.Status);
    }

    [Fact]
    public async Task SaveCipherAndGetId_CreateTransportFailure_WithMultipleWitnessesThrowsSnapshotMismatch()
    {
        _api.CreateFailure = new HttpRequestException("response lost");
        _sync.ResultFactory = () =>
        [
            CreatedCipherFromLastRequest("created-a"),
            CreatedCipherFromLastRequest("created-b"),
        ];

        var thrown = await Assert.ThrowsAsync<CipherCreateSnapshotMismatchException>(() =>
            NewUnlockedService().SaveCipherAndGetIdAsync(
                NewLogin(),
                TestContext.Current.CancellationToken));

        Assert.True(thrown.Pending.CommitConfirmed);
        Assert.Equal(CipherCreateReconciliationStatus.Ambiguous, thrown.Reconciliation.Status);
        Assert.Equal(["created-a", "created-b"], thrown.Reconciliation.CandidateIds);
    }

    [Fact]
    public async Task SaveCipherAndGetId_CreateClientRejection_DoesNotSync()
    {
        var rejection = new VaultWriteException(
            HttpStatusCode.BadRequest,
            "invalid cipher",
            validationErrors: null);
        _api.CreateFailure = rejection;
        var service = NewUnlockedService();

        var thrown = await Assert.ThrowsAsync<VaultWriteException>(() =>
            service.SaveCipherAndGetIdAsync(
                NewLogin(),
                TestContext.Current.CancellationToken));

        Assert.Same(rejection, thrown);
        Assert.Equal(0, _sync.Calls);
        Assert.False(service.HasPendingOrInFlightCipherCreate());
        Assert.Null(service.GetPendingCipherCreate());
    }

    [Fact]
    public async Task ReconcileCipherCreate_ConfirmedResultClearsRetainedWriterGate()
    {
        _api.CreateFailure = new HttpRequestException("response lost");
        _sync.ResultFactory = () => Array.Empty<Cipher>();
        var service = NewUnlockedService();
        var thrown = await Assert.ThrowsAsync<CipherCreateSnapshotMismatchException>(() =>
            service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None));
        Assert.NotNull(service.GetPendingCipherCreate());

        _sync.ResultFactory = DefaultSyncResult;
        var reconciliation = await service.SyncAndReconcileCipherCreateAsync(
            thrown.Pending,
            CancellationToken.None);

        Assert.Equal(CipherCreateReconciliationStatus.Confirmed, reconciliation.Status);
        Assert.False(service.HasPendingOrInFlightCipherCreate());
        Assert.Null(service.GetPendingCipherCreate());
    }

    [Fact]
    public async Task ReconcileCipherCreate_DifferentFingerprintCannotReplaceRetainedPending()
    {
        _api.CreateFailure = new HttpRequestException("response lost");
        _sync.ResultFactory = () => Array.Empty<Cipher>();
        var service = NewUnlockedService();
        var thrown = await Assert.ThrowsAsync<CipherCreateSnapshotMismatchException>(() =>
            service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None));
        var retained = Assert.IsType<CipherCreatePending>(service.GetPendingCipherCreate());
        var conflicting = new CipherCreatePending(
            new CipherCreateWitness(
                retained.Witness.AccountId,
                retained.Witness.Endpoints,
                retained.Witness.CipherType,
                retained.Witness.OrganizationId,
                retained.Witness.CollectionIds,
                retained.Witness.BaselineCipherIds,
                "A1B2C3"),
            CommitConfirmed: true,
            ExpectedCipherId: "different-id");

        await Assert.ThrowsAsync<CipherCreatePendingOperationException>(() =>
            service.SyncAndReconcileCipherCreateAsync(conflicting, CancellationToken.None));

        Assert.Equal(1, _sync.Calls);
        var stillRetained = Assert.IsType<CipherCreatePending>(service.GetPendingCipherCreate());
        Assert.Equal(thrown.Pending.Witness.KeyFingerprint, stillRetained.Witness.KeyFingerprint);
        Assert.NotEqual(conflicting.Witness.KeyFingerprint, stillRetained.Witness.KeyFingerprint);
        Assert.Null(stillRetained.ExpectedCipherId);
    }

    [Fact]
    public async Task SaveCipherAndGetId_ResponseIdMismatchThrowsSnapshotMismatch()
    {
        _api.CreateResponseId = "response-created";
        _sync.ResultFactory = () => [CreatedCipherFromLastRequest("snapshot-created")];

        var thrown = await Assert.ThrowsAsync<CipherCreateSnapshotMismatchException>(() =>
            NewUnlockedService().SaveCipherAndGetIdAsync(
                NewLogin(),
                TestContext.Current.CancellationToken));

        Assert.True(thrown.Pending.CommitConfirmed);
        Assert.Equal("response-created", thrown.Pending.ExpectedCipherId);
        Assert.Equal(CipherCreateReconciliationStatus.SnapshotMismatch, thrown.Reconciliation.Status);
        Assert.Equal(["snapshot-created"], thrown.Reconciliation.CandidateIds);
    }

    [Fact]
    public async Task SaveCipherAndGetId_CommittedResponse_UsesNonCancelableSync()
    {
        _api.CreateResponseId = "server-created-1";

        var id = await NewUnlockedService().SaveCipherAndGetIdAsync(
            NewLogin(),
            new CancellationToken(canceled: true));

        Assert.Equal("server-created-1", id);
        Assert.False(_sync.LastTokenWasCancellationRequested);
    }

    [Fact]
    public async Task ReconcileCipherCreate_ExcludesBaselineIdsBeforeConfirmingUniqueCandidate()
    {
        const string key = "2.same-wrapped-key";
        var baseline = new Cipher
        {
            Id = "baseline-id",
            Type = CipherType.Login,
            Key = key,
            Name = "Baseline",
        };
        var created = new Cipher
        {
            Id = "created-id",
            Type = CipherType.Login,
            Key = key,
            Name = "Created",
        };
        var (service, session) = NewUnlockedServiceWithSnapshot(baseline, created);
        _sync.ResultFactory = () => [baseline, created];
        var lease = session.CaptureLease();
        var witness = new CipherCreateWitness(
            lease.AccountId,
            lease.Endpoints,
            CipherType.Login,
            organizationId: null,
            collectionIds: Array.Empty<string>(),
            baselineCipherIds: ["baseline-id", "baseline-id"],
            keyFingerprint: CipherCreateWitness.ComputeKeyFingerprint(key));

        var pending = new CipherCreatePending(
            witness,
            CommitConfirmed: false,
            ExpectedCipherId: null);
        _pendingStore.Seed(pending);

        var result = await service.SyncAndReconcileCipherCreateAsync(
            pending,
            TestContext.Current.CancellationToken);

        Assert.Equal(CipherCreateReconciliationStatus.Confirmed, result.Status);
        Assert.Equal("created-id", result.ConfirmedId);
        Assert.Equal(["created-id"], result.CandidateIds);
        Assert.Equal(["baseline-id"], witness.BaselineCipherIds);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)witness.BaselineCipherIds).Add("mutation"));
    }

    [Theory]
    [InlineData(CipherType.BankAccount)]
    [InlineData(CipherType.DriversLicense)]
    [InlineData(CipherType.Passport)]
    public async Task SaveCipher_TypeWithoutNegotiatedWriteSupport_IsRejectedBeforeApiCall(
        CipherType type)
    {
        var exception = await Assert.ThrowsAsync<UnsupportedCipherTypeWriteException>(() =>
            NewUnlockedService().SaveCipherAsync(
                new Cipher { Type = type, Name = "Unsupported" },
                TestContext.Current.CancellationToken));

        Assert.Equal(type, exception.CipherType);
        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Theory]
    [InlineData(CipherType.BankAccount)]
    [InlineData(CipherType.DriversLicense)]
    [InlineData(CipherType.Passport)]
    public async Task SaveCipher_NegotiatedNewCipherType_IsWritten(CipherType type)
    {
        await NewUnlockedService(NewCipherTypesEnabled).SaveCipherAsync(
            NewCipherType(type),
            TestContext.Current.CancellationToken);

        Assert.Equal("create", Assert.Single(_api.Calls));
        Assert.Equal((int)type, _api.LastCipher!.Type);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_InvalidTypedShape_IsRejectedBeforeApiCall()
    {
        var exception = await Assert.ThrowsAsync<InvalidCipherWriteShapeException>(() =>
            NewUnlockedService().SaveCipherAsync(
                new Cipher { Type = CipherType.Login, Name = "Missing login" },
                TestContext.Current.CancellationToken));

        Assert.Equal(CipherType.Login, exception.CipherType);
        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_WriteSucceedsButResyncFails_ThrowsCreateOutcomeUncertain()
    {
        var session = new VaultSession();
        session.BeginAccountSession("test-account", "test@test.com", new Core.Models.ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var innerEx = new HttpRequestException("network down");
        var svc = new VaultWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api),
            new CipherEncryptor(new CryptoService()),
            new ThrowingSync(innerEx),
            session,
            cipherCreateStore: _pendingStore);
        _api.CreateResponseId = "server-created-1";

        var ex = await Assert.ThrowsAsync<CipherCreateOutcomeUncertainException>(
            () => svc.SaveCipherAsync(NewLogin(), TestContext.Current.CancellationToken));

        Assert.Same(innerEx, ex.RefreshException);
        Assert.Null(ex.MutationException);
        Assert.True(ex.Pending.CommitConfirmed);
        Assert.Equal("server-created-1", ex.Pending.ExpectedCipherId);
        Assert.True(session.DataStale);
        Assert.Equal("create", Assert.Single(_api.Calls));
    }

    [Fact]
    public async Task SaveCipher_CommittedCreateReconciliationCancelled_ThrowsCreateOutcomeUncertain()
    {
        var session = new VaultSession();
        session.BeginAccountSession("test-account", "test@test.com", new Core.Models.ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var svc = new VaultWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api),
            new CipherEncryptor(new CryptoService()),
            new ThrowingSync(new OperationCanceledException()),
            session,
            cipherCreateStore: _pendingStore);

        var thrown = await Assert.ThrowsAsync<CipherCreateOutcomeUncertainException>(
            () => svc.SaveCipherAsync(NewLogin(), CancellationToken.None));

        Assert.True(thrown.Pending.CommitConfirmed);
        Assert.IsType<OperationCanceledException>(thrown.RefreshException);
        Assert.True(session.DataStale);
    }

    [Fact]
    public async Task SaveCipher_CreateFailureAndRecoverySyncFailure_ThrowsCreateOutcomeUncertain()
    {
        var mutationFailure = new HttpRequestException("response lost");
        var refreshFailure = new IOException("sync failed");
        _api.CreateFailure = mutationFailure;
        _sync.Failure = refreshFailure;
        var (service, session) = NewUnlockedServiceWithSnapshot();

        var thrown = await Assert.ThrowsAsync<CipherCreateOutcomeUncertainException>(() =>
            service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None));

        Assert.False(thrown.Pending.CommitConfirmed);
        Assert.Same(mutationFailure, thrown.MutationException);
        Assert.Same(refreshFailure, thrown.RefreshException);
        Assert.True(session.DataStale);
        Assert.False(_sync.LastTokenWasCancellationRequested);
    }

    [Fact]
    public async Task SaveCipher_CreateRecoverySessionChange_ThrowsCreateOutcomeUncertain()
    {
        _api.CreateFailure = new HttpRequestException("response lost");
        var (service, session) = NewUnlockedServiceWithSnapshot();
        _sync.BeforeCompletion = () =>
        {
            session.BeginAccountSession(
                "replacement-account",
                "replacement@example.com",
                new ServerOrigin("https://replacement.example"));
            session.SetUnlockedKey(new SymmetricCryptoKey(
                Enumerable.Range(0, 64).Select(i => (byte)(255 - i)).ToArray()));
        };

        var thrown = await Assert.ThrowsAsync<CipherCreateOutcomeUncertainException>(() =>
            service.SaveCipherAndGetIdAsync(NewLogin(), CancellationToken.None));

        Assert.False(thrown.Pending.CommitConfirmed);
        Assert.Equal("test-account", thrown.Pending.Witness.AccountId);
        Assert.IsType<OperationCanceledException>(thrown.RefreshException);
        Assert.False(session.DataStale);
    }

    private sealed class ThrowingSync : ISyncService
    {
        private readonly Exception _ex;
        public ThrowingSync(Exception ex) => _ex = ex;
        public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default) => Task.FromException<IReadOnlyList<Cipher>>(_ex);

        public Task<IReadOnlyList<Cipher>> SyncAsync(
            Core.Session.VaultSessionLease lease, CancellationToken ct = default) => SyncAsync(ct);
    }


    [Fact]
    public async Task SaveCipher_ExistingItem_UpdatesWithId()
    {
        var cipher = NewLogin("c-9");
        var (service, _) = NewUnlockedServiceWithSnapshot(cipher);

        await service.SaveCipherAsync(cipher, TestContext.Current.CancellationToken);

        Assert.Equal("update", Assert.Single(_api.Calls));
        Assert.Equal("c-9", _api.LastId);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_MissingPersonalItem_IsRejectedBeforeApiCall()
    {
        var (service, _) = NewUnlockedServiceWithSnapshot();

        await Assert.ThrowsAsync<CipherEditConflictException>(() =>
            service.SaveCipherAsync(NewLogin("missing"), TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_ExistingItem_UsesOpeningRevisionInWriteRequest()
    {
        var revision = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var cipher = NewLogin("c-9").WithMetadata(
            "c-9",
            revision.AddDays(-1),
            revision);
        var (service, _) = NewUnlockedServiceWithSnapshot(cipher);

        await service.SaveCipherAsync(cipher, TestContext.Current.CancellationToken);

        Assert.Equal(revision.ToString("O"), _api.LastCipher!.LastKnownRevisionDate);
    }

    [Fact]
    public async Task SaveCipher_ArchivedItem_PreservesArchiveStateInFullUpdate()
    {
        var archivedDate = new DateTimeOffset(2026, 8, 1, 12, 30, 0, TimeSpan.Zero);
        var cipher = new Cipher
        {
            Id = "archived-1",
            Type = CipherType.Login,
            Name = "Archived",
            ArchivedDate = archivedDate,
            Login = new CipherLogin("user", "password", null, []),
        };
        var (service, _) = NewUnlockedServiceWithSnapshot(cipher);

        await service.SaveCipherAsync(cipher, TestContext.Current.CancellationToken);

        Assert.Equal(archivedDate, _api.LastCipher!.ArchivedDate);
    }

    [Fact]
    public async Task SaveCipher_SnapshotRevisionChangedAfterDraftMapping_IsRejectedBeforeApiCall()
    {
        var openedRevision = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var current = NewLogin("c-9").WithMetadata(
            "c-9",
            openedRevision.AddDays(-1),
            openedRevision.AddSeconds(1));
        var staleEdit = NewLogin("c-9").WithMetadata(
            "c-9",
            current.CreationDate,
            openedRevision);
        var (service, _) = NewUnlockedServiceWithSnapshot(current);

        await Assert.ThrowsAsync<CipherEditConflictException>(() =>
            service.SaveCipherAsync(staleEdit, TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_ExistingItemTypeChange_IsRejectedBeforeApiCall()
    {
        var revision = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var current = NewLogin("c-9").WithMetadata("c-9", revision.AddDays(-1), revision);
        var changedType = new Cipher
        {
            Id = current.Id,
            Type = CipherType.Card,
            Name = current.Name,
            CreationDate = current.CreationDate,
            RevisionDate = current.RevisionDate,
            Card = new CipherCard("User", "4111111111111111", "12", "2030", "123", "Visa"),
        };
        var (service, _) = NewUnlockedServiceWithSnapshot(current);

        await Assert.ThrowsAsync<CipherEditConflictException>(() =>
            service.SaveCipherAsync(changedType, TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_HiddenPasswordItem_IsRejectedAtWriteServiceBoundary()
    {
        var revision = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var current = new Cipher
        {
            Id = "org-item",
            Type = CipherType.Login,
            OrganizationId = "org-1",
            CollectionIds = ["collection-1"],
            Key = "2.wrapped-item-key",
            Name = "Hidden",
            CreationDate = revision.AddDays(-1),
            RevisionDate = revision,
            CanEdit = true,
            CanViewPassword = false,
            Login = new CipherLogin("user", null, null, []),
        };
        var (service, _) = NewUnlockedServiceWithSnapshot(current);

        await Assert.ThrowsAsync<OrganizationCipherOperationException>(() =>
            service.SaveCipherAsync(current, TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_ExistingUnsupportedType_CannotBeChangedToSupportedType()
    {
        var existing = new Cipher
        {
            Id = "bank-1",
            Type = CipherType.BankAccount,
            Name = "Existing bank account",
            BankAccount = new CipherBankAccount(
                "Example Bank", null, null, "1234", null, null, null, null, null, null),
        };
        var (service, _) = NewUnlockedServiceWithSnapshot(existing);

        var exception = await Assert.ThrowsAsync<UnsupportedCipherTypeWriteException>(() =>
            service.SaveCipherAsync(
                NewLogin(existing.Id),
                TestContext.Current.CancellationToken));

        Assert.Equal(CipherType.BankAccount, exception.CipherType);
        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_NewOrganizationItem_RecoversLostResponseWithCollectionBoundWitness()
    {
        var crypto = new CryptoService();
        using var rsa = RSA.Create(2048);
        var userKey = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        using var organizationKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var privateKeyBytes = rsa.ExportPkcs8PrivateKey();
        var encryptedPrivateKey = crypto.Encrypt(privateKeyBytes, userKey).ToString();
        CryptographicOperations.ZeroMemory(privateKeyBytes);
        var encryptedOrganizationKey = crypto.EncryptRsa(
            organizationKey.FullKey,
            rsa.ExportSubjectPublicKeyInfo()).ToString();
        var session = new VaultSession();
        session.BeginAccountSession("test-account", "test@test.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(userKey);
        session.SetEncryptedPrivateKey(encryptedPrivateKey);
        session.SetSnapshot(new DecryptedVault(session.Account, [], [], 0, 0)
        {
            Organizations =
            [
                new VaultOrganization("org-1", "Team", 2, 2, true)
                {
                    EncryptedKey = encryptedOrganizationKey,
                },
            ],
            Collections =
            [
                new VaultCollection("collection-1", "org-1", "Shared", false, false, false, null),
                new VaultCollection("collection-2", "org-1", "Engineering", false, false, false, null),
            ],
        });
        var cipher = new Cipher
        {
            Type = CipherType.Login,
            OrganizationId = "org-1",
            CollectionIds = ["collection-1", "collection-2"],
            Name = "New shared item",
            Login = new CipherLogin("shared-user", "shared-password", null, []),
        };
        var service = new VaultWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api),
            new CipherEncryptor(crypto),
            _sync,
            session,
            new OrganizationKeyResolver(crypto),
            _pendingStore);
        _api.CreateFailure = new HttpRequestException("response lost");

        var id = await service.SaveCipherAndGetIdAsync(
            cipher,
            TestContext.Current.CancellationToken);

        Assert.Equal("synced-created-1", id);
        Assert.Equal("org-create", Assert.Single(_api.Calls));
        Assert.Equal(["collection-1", "collection-2"], _api.LastSharedCipher!.CollectionIds);
        Assert.Equal("org-1", _api.LastSharedCipher.Cipher.OrganizationId);
        using var itemKey = crypto.DecryptItemKey(_api.LastSharedCipher.Cipher.Key!, organizationKey);
        Assert.Equal("New shared item", crypto.DecryptToString(_api.LastSharedCipher.Cipher.Name, itemKey));
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_NewOrganizationItemWithoutCollection_IsRejectedBeforeApiCall()
    {
        var crypto = new CryptoService();
        var userKey = new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        using var organizationKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var session = new VaultSession();
        session.BeginAccountSession(
            "test-account",
            "test@test.com",
            new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(userKey);
        session.SetSnapshot(new DecryptedVault(session.Account, [], [], 0, 0)
        {
            Organizations =
            [
                new VaultOrganization("org-1", "Team", 2, 0, true)
                {
                    EncryptedKey = crypto.Encrypt(organizationKey.FullKey, userKey).ToString(),
                },
            ],
        });
        var cipher = new Cipher
        {
            Type = CipherType.Login,
            OrganizationId = "org-1",
            Name = "Unassigned shared item",
            Login = new CipherLogin("user", "password", null, []),
        };
        var service = new VaultWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api),
            new CipherEncryptor(crypto),
            _sync,
            session,
            new OrganizationKeyResolver(crypto),
            _pendingStore);

        var exception = await Assert.ThrowsAsync<OrganizationCipherOperationException>(() =>
            service.SaveCipherAsync(cipher, TestContext.Current.CancellationToken));

        Assert.Contains("at least one writable collection", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_OrganizationItem_IsRejectedBeforeApiCall()
    {
        var cipher = NewLogin("org-item", "org-1");

        await Assert.ThrowsAsync<OrganizationCipherOperationException>(() =>
            NewUnlockedService().SaveCipherAsync(cipher, TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_EditableOrganizationItem_UsesOrganizationKeyAndPreservesItemKey()
    {
        var crypto = new CryptoService();
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        using var organizationKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        using var itemKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var userKey = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var privateKeyBytes = rsa.ExportPkcs8PrivateKey();
        var encryptedPrivateKey = crypto.Encrypt(privateKeyBytes, userKey).ToString();
        CryptographicOperations.ZeroMemory(privateKeyBytes);
        var encryptedOrganizationKey = crypto.EncryptRsa(
            organizationKey.FullKey,
            rsa.ExportSubjectPublicKeyInfo()).ToString();
        var wrappedItemKey = crypto.Encrypt(itemKey.FullKey, organizationKey).ToString();
        var cipher = NewLogin(
            "org-item",
            "org-1",
            canEdit: true,
            canDelete: true,
            canRestore: true);
        cipher = new Cipher
        {
            Id = cipher.Id,
            OrganizationId = cipher.OrganizationId,
            CollectionIds = ["collection-1"],
            Key = wrappedItemKey,
            Type = cipher.Type,
            Name = "Edited organization item",
            Login = cipher.Login,
            CanEdit = true,
            CanDelete = true,
            CanRestore = true,
        };
        var session = new VaultSession();
        session.BeginAccountSession("test-account", "test@test.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(userKey);
        session.SetEncryptedPrivateKey(encryptedPrivateKey);
        session.SetSnapshot(new DecryptedVault(session.Account, [], [cipher], 0, 0)
        {
            Organizations =
            [
                new VaultOrganization("org-1", "Team", 2, 2, true)
                {
                    EncryptedKey = encryptedOrganizationKey,
                },
            ],
            Collections = [new VaultCollection("collection-1", "org-1", "Shared", false, false, false, null)],
        });
        var service = new VaultWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api),
            new CipherEncryptor(crypto),
            _sync,
            session,
            new OrganizationKeyResolver(crypto),
            _pendingStore);

        await service.SaveCipherAsync(cipher, TestContext.Current.CancellationToken);

        Assert.Equal("update", Assert.Single(_api.Calls));
        Assert.Equal("org-1", _api.LastCipher!.OrganizationId);
        Assert.Equal(wrappedItemKey, _api.LastCipher.Key);
        Assert.Equal("Edited organization item", crypto.DecryptToString(_api.LastCipher.Name, itemKey));
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_PersonalOwnershipPolicy_IsRejectedBeforeApiCall()
    {
        var (service, session) = NewUnlockedServiceWithSnapshot();
        session.SetSnapshot(new DecryptedVault(session.Account, [], [], 0, 0)
        {
            Organizations = [new VaultOrganization("org-1", "Team", 2, 2, true)],
            Policies =
            [
                new VaultOrganizationPolicy(
                    "policy-1",
                    "org-1",
                    (int)OrganizationPolicyType.PersonalOwnership,
                    true,
                    "{}"),
            ],
        });

        await Assert.ThrowsAsync<OrganizationPolicyException>(() =>
            service.SaveCipherAsync(NewLogin(), TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task BatchMutation_ContainingOrganizationItem_IsRejectedBeforeApiCall()
    {
        var personal = NewLogin("personal-item");
        var organization = NewLogin("org-item", "org-1");
        var (service, _) = NewUnlockedServiceWithSnapshot(personal, organization);

        await Assert.ThrowsAsync<OrganizationCipherOperationException>(() =>
            service.DeleteCiphersAsync(
                [personal.Id, organization.Id],
                permanent: false,
                TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task DeleteCipher_SoftByDefault()
    {
        await NewUnlockedService().DeleteCipherAsync("c-9", permanent: false, TestContext.Current.CancellationToken);

        Assert.Equal("soft", Assert.Single(_api.Calls));
        Assert.Equal("c-9", _api.LastId);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task DeleteCipher_PermanentHardDeletes()
    {
        await NewUnlockedService().DeleteCipherAsync("c-9", permanent: true, TestContext.Current.CancellationToken);

        Assert.Equal("hard", Assert.Single(_api.Calls));
        Assert.Equal("c-9", _api.LastId);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task RestoreCipher_RestoresThenResyncs()
    {
        await NewUnlockedService().RestoreCipherAsync("c-9", TestContext.Current.CancellationToken);

        Assert.Equal("restore", Assert.Single(_api.Calls));
        Assert.Equal("c-9", _api.LastId);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task DeleteCipher_OrganizationItemWithPermission_IsAllowed()
    {
        var organization = NewLogin(
            "org-item",
            "org-1",
            canDelete: true);
        var (service, _) = NewUnlockedServiceWithSnapshot(organization);

        await service.DeleteCipherAsync(
            organization.Id,
            permanent: false,
            TestContext.Current.CancellationToken);

        Assert.Equal("soft", Assert.Single(_api.Calls));
        Assert.Equal(organization.Id, _api.LastId);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task ArchiveCipher_ArchivesThenResyncs()
    {
        await NewUnlockedService(ArchiveEnabled).ArchiveCipherAsync(
            "c-9",
            TestContext.Current.CancellationToken);

        Assert.Equal("archive", Assert.Single(_api.Calls));
        Assert.Equal("c-9", _api.LastId);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task UnarchiveCipher_OrganizationItem_IsAllowedBecauseArchiveIsPerUserMetadata()
    {
        var organization = NewLogin("org-item", "org-1");
        var (service, _) = NewUnlockedServiceWithSnapshot(ArchiveProtocolOnly, organization);

        await service.UnarchiveCipherAsync(organization.Id, TestContext.Current.CancellationToken);

        Assert.Equal("unarchive", Assert.Single(_api.Calls));
        Assert.Equal(organization.Id, _api.LastId);
        Assert.Equal(1, _sync.Calls);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, true)]
    public async Task SetCipherArchived_RequiresOperationSpecificCapability(
        bool archive,
        bool supportsProtocol,
        bool hasEntitlement,
        bool allowed)
    {
        var service = NewUnlockedService(
            new VaultCapabilities(false, supportsProtocol, hasEntitlement));
        Task Operation() => archive
            ? service.ArchiveCipherAsync("c-9", TestContext.Current.CancellationToken)
            : service.UnarchiveCipherAsync("c-9", TestContext.Current.CancellationToken);

        if (allowed)
        {
            await Operation();
            Assert.Equal(archive ? "archive" : "unarchive", Assert.Single(_api.Calls));
            Assert.Equal(1, _sync.Calls);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<UnsupportedCipherArchiveOperationException>(Operation);
            Assert.Equal(archive, exception.Archive);
            Assert.Empty(_api.Calls);
            Assert.Equal(0, _sync.Calls);
        }
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, true)]
    public async Task SetCiphersArchived_RequiresOperationSpecificCapability(
        bool archive,
        bool supportsProtocol,
        bool hasEntitlement,
        bool allowed)
    {
        var service = NewUnlockedService(
            new VaultCapabilities(false, supportsProtocol, hasEntitlement));
        Task Operation() => archive
            ? service.ArchiveCiphersAsync(["a"], TestContext.Current.CancellationToken)
            : service.UnarchiveCiphersAsync(["a"], TestContext.Current.CancellationToken);

        if (allowed)
        {
            await Operation();
            Assert.Equal(
                archive ? "bulk-archive" : "bulk-unarchive",
                Assert.Single(_api.Calls));
            Assert.Equal(1, _sync.Calls);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<UnsupportedCipherArchiveOperationException>(Operation);
            Assert.Equal(archive, exception.Archive);
            Assert.Empty(_api.Calls);
            Assert.Equal(0, _sync.Calls);
        }
    }

    [Fact]
    public async Task SetCipherFavorite_OrganizationItem_UsesPartialUpdateAndPreservesFolder()
    {
        var organization = NewLogin("org-item", "org-1", "folder-1");
        var (service, _) = NewUnlockedServiceWithSnapshot(organization);

        await service.SetCipherFavoriteAsync(
            organization.Id,
            favorite: true,
            TestContext.Current.CancellationToken);

        Assert.Equal("partial", Assert.Single(_api.Calls));
        Assert.Equal(organization.Id, _api.LastId);
        Assert.Equal("folder-1", _api.LastPartialCipher!.FolderId);
        Assert.True(_api.LastPartialCipher.Favorite);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task SaveFolder_New_EncryptsNameAndCreates()
    {
        await NewUnlockedService().SaveFolderAsync(null, "Work", TestContext.Current.CancellationToken);

        Assert.Equal("folder-create", Assert.Single(_api.Calls));
        Assert.StartsWith("2.", _api.LastFolder!.Name);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task SaveFolder_Existing_Updates()
    {
        await NewUnlockedService().SaveFolderAsync("f-1", "Work", TestContext.Current.CancellationToken);

        Assert.Equal("folder-update", Assert.Single(_api.Calls));
        Assert.Equal("f-1", _api.LastId);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task DeleteFolder_DeletesThenResyncs()
    {
        await NewUnlockedService().DeleteFolderAsync("f-1", TestContext.Current.CancellationToken);

        Assert.Equal("folder-delete", Assert.Single(_api.Calls));
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api), new CipherEncryptor(new CryptoService()), _sync, new VaultSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveCipherAsync(NewLogin(), TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task DeleteCipher_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api), new CipherEncryptor(new CryptoService()), _sync, new VaultSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteCipherAsync("c-9", permanent: false, TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task RestoreCipher_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api), new CipherEncryptor(new CryptoService()), _sync, new VaultSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreCipherAsync("c-9", TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task SaveFolder_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api), new CipherEncryptor(new CryptoService()), _sync, new VaultSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveFolderAsync(null, "Work", TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task DeleteFolder_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api), new CipherEncryptor(new CryptoService()), _sync, new VaultSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteFolderAsync("f-1", TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task MoveCiphers_CallsBulkMoveThenResyncs()
    {
        await NewUnlockedService().MoveCiphersAsync(new[] { "a", "b" }, "f1", TestContext.Current.CancellationToken);

        Assert.Equal("bulk-move", Assert.Single(_api.Calls));
        Assert.Equal(new[] { "a", "b" }, _api.LastIds);
        Assert.Equal("f1", _api.LastFolderId);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task DeleteCiphers_Soft_CallsBulkSoft()
    {
        await NewUnlockedService().DeleteCiphersAsync(new[] { "a" }, permanent: false, TestContext.Current.CancellationToken);
        Assert.Equal("bulk-soft", Assert.Single(_api.Calls));
        Assert.Equal(new[] { "a" }, _api.LastIds);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task DeleteCiphers_Permanent_CallsBulkHard()
    {
        await NewUnlockedService().DeleteCiphersAsync(new[] { "a" }, permanent: true, TestContext.Current.CancellationToken);
        Assert.Equal("bulk-hard", Assert.Single(_api.Calls));
        Assert.Equal(new[] { "a" }, _api.LastIds);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task RestoreCiphers_CallsBulkRestore()
    {
        await NewUnlockedService().RestoreCiphersAsync(new[] { "a" }, TestContext.Current.CancellationToken);
        Assert.Equal("bulk-restore", Assert.Single(_api.Calls));
        Assert.Equal(1, _sync.Calls);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("delete")]
    [InlineData("restore")]
    [InlineData("archive")]
    [InlineData("unarchive")]
    public async Task BatchMutation_ApiFailure_ForcesLeaseBoundAuthoritativeSync(string operation)
    {
        var failure = new HttpRequestException("response lost");
        _api.BulkFailure = failure;
        var capabilities = operation switch
        {
            "archive" => ArchiveEnabled,
            "unarchive" => ArchiveProtocolOnly,
            _ => VaultCapabilities.None,
        };
        var (service, session) = NewUnlockedServiceWithSnapshot(capabilities, NewLogin("a"));
        var expectedLease = session.CaptureLease();

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => operation switch
        {
            "move" => service.MoveCiphersAsync(["a"], "f1", new CancellationToken(true)),
            "delete" => service.DeleteCiphersAsync(["a"], permanent: false, new CancellationToken(true)),
            "restore" => service.RestoreCiphersAsync(["a"], new CancellationToken(true)),
            "archive" => service.ArchiveCiphersAsync(["a"], new CancellationToken(true)),
            _ => service.UnarchiveCiphersAsync(["a"], new CancellationToken(true)),
        });

        Assert.Same(failure, thrown);
        Assert.Equal(1, _sync.Calls);
        Assert.Equal(expectedLease.Generation, _sync.LastLease!.Generation);
        Assert.Equal(expectedLease.AccountId, _sync.LastLease.AccountId);
        Assert.False(_sync.LastTokenWasCancellationRequested);
        Assert.False(session.DataStale);
    }

    [Fact]
    public async Task BatchMutation_ApiAndRecoverySyncFail_MarksSnapshotStaleAndThrowsDedicatedException()
    {
        var mutationFailure = new TimeoutException("request timed out");
        var refreshFailure = new HttpRequestException("sync unavailable");
        _api.BulkFailure = mutationFailure;
        _sync.Failure = refreshFailure;
        var (service, session) = NewUnlockedServiceWithSnapshot(NewLogin("a"));

        var thrown = await Assert.ThrowsAsync<VaultBatchMutationRecoveryException>(() =>
            service.DeleteCiphersAsync(["a"], permanent: false, CancellationToken.None));

        Assert.Same(mutationFailure, thrown.MutationException);
        Assert.Same(refreshFailure, thrown.RefreshException);
        Assert.True(session.DataStale);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task BatchMutation_RecoveryCannotMarkReplacementAccountStale()
    {
        var mutationFailure = new HttpRequestException("response lost");
        _api.BulkFailure = mutationFailure;
        _sync.Failure = new HttpRequestException("old account sync failed");
        var (service, session) = NewUnlockedServiceWithSnapshot(NewLogin("a"));
        _sync.BeforeCompletion = () =>
        {
            session.BeginAccountSession(
                "replacement-account",
                "replacement@example.com",
                new ServerOrigin("https://replacement.example"));
            session.SetUnlockedKey(new SymmetricCryptoKey(
                Enumerable.Range(0, 64).Select(i => (byte)(255 - i)).ToArray()));
        };

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.MoveCiphersAsync(["a"], "f1", CancellationToken.None));

        Assert.Same(mutationFailure, thrown);
        Assert.Equal("replacement-account", session.Account.Id);
        Assert.False(session.DataStale);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task BatchMutation_ApiSucceedsButSyncFails_MarksSnapshotStale()
    {
        _sync.Failure = new HttpRequestException("sync unavailable");
        var (service, session) = NewUnlockedServiceWithSnapshot(NewLogin("a"));

        await Assert.ThrowsAsync<VaultRefreshException>(() =>
            service.MoveCiphersAsync(["a"], "f1", CancellationToken.None));

        Assert.True(session.DataStale);
    }

    [Fact]
    public async Task MoveCiphers_OrganizationItem_IsAllowedBecauseFolderIsPerUserMetadata()
    {
        var organization = NewLogin("org-item", "org-1");
        var (service, _) = NewUnlockedServiceWithSnapshot(organization);

        await service.MoveCiphersAsync(
            new[] { organization.Id },
            "folder-1",
            TestContext.Current.CancellationToken);

        Assert.Equal("bulk-move", Assert.Single(_api.Calls));
        Assert.Equal(new[] { organization.Id }, _api.LastIds);
        Assert.Equal("folder-1", _api.LastFolderId);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task ArchiveAndUnarchiveCiphers_CallBulkEndpointsAndResync()
    {
        var service = NewUnlockedService(ArchiveEnabled);

        await service.ArchiveCiphersAsync(new[] { "a", "b" }, TestContext.Current.CancellationToken);
        await service.UnarchiveCiphersAsync(new[] { "c", "d" }, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "bulk-archive", "bulk-unarchive" }, _api.Calls);
        Assert.Equal(new[] { "c", "d" }, _api.LastIds);
        Assert.Equal(2, _sync.Calls);
    }

    [Fact]
    public async Task ArchiveCipher_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api),
            new CipherEncryptor(new CryptoService()),
            _sync,
            new VaultSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ArchiveCipherAsync("c-9", TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task DeleteCiphers_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api), new CipherEncryptor(new CryptoService()), _sync, new VaultSession());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteCiphersAsync(new[] { "a" }, permanent: false, TestContext.Current.CancellationToken));
        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task RestoreCiphers_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api), new CipherEncryptor(new CryptoService()), _sync, new VaultSession());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreCiphersAsync(new[] { "a" }, TestContext.Current.CancellationToken));
        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task MoveCiphers_EmptyIds_NoApiNoSync()
    {
        await NewUnlockedService().MoveCiphersAsync(Array.Empty<string>(), "f1", TestContext.Current.CancellationToken);
        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task MoveCiphers_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api), new CipherEncryptor(new CryptoService()), _sync, new VaultSession());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MoveCiphersAsync(new[] { "a" }, null, TestContext.Current.CancellationToken));
        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }
}
