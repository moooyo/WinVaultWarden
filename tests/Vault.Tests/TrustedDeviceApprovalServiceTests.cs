using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class TrustedDeviceApprovalServiceTests
{
    private const string DeviceIdentifier = "11111111-2222-3333-4444-555555555555";
    private readonly CryptoService _crypto = new();

    [Theory]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64)]
    public async Task BeginAndPoll_ApprovedResponseDecryptsUserKey(
        EncryptionType envelopeType)
    {
        var session = CreateLockedAuthenticatedSession();
        var lease = session.CaptureLease();
        var api = new FakeApprovalApi();
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IPublicAuthRequestApiClient>(api)
            .AddAuthenticated<IAuthRequestApiClient>(api);
        var fingerprintService = new FingerprintService();
        var service = new TrustedDeviceApprovalService(
            provider,
            _crypto,
            fingerprintService,
            session);

        var request = await service.BeginAsync(
            lease,
            "me@example.com",
            DeviceIdentifier,
            TestContext.Current.CancellationToken);

        Assert.Equal("request-id", request.Id);
        Assert.Equal(25, api.Created!.AccessCode.Length);
        Assert.Equal(DeviceIdentifier, api.Created.DeviceIdentifier);
        var fingerprintPublicKey = Convert.FromBase64String(api.Created.PublicKey);
        try
        {
            Assert.Equal(
                fingerprintService.GetFingerprintPhrase(
                    fingerprintPublicKey,
                    "me@example.com"),
                request.FingerprintPhrase);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fingerprintPublicKey);
        }
        var pending = await service.PollAsync(
            lease,
            request.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(TrustedDeviceApprovalStatus.Pending, pending.Status);

        var expectedUserKey = Enumerable.Range(1, 64)
            .Select(value => (byte)value)
            .ToArray();
        var publicKey = Convert.FromBase64String(api.Created.PublicKey);
        api.ApprovalKey = EncryptForType(
            expectedUserKey,
            publicKey,
            envelopeType).ToString();
        var approved = await service.PollAsync(
            lease,
            request.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(TrustedDeviceApprovalStatus.Approved, approved.Status);
        Assert.NotNull(approved.UserKey);
        try
        {
            Assert.Equal(expectedUserKey, approved.UserKey);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                approved.UserKey!);
        }
    }

    [Fact]
    public async Task Poll_DeniedResponseClearsPendingRequest()
    {
        var session = CreateLockedAuthenticatedSession();
        var lease = session.CaptureLease();
        var api = new FakeApprovalApi { Denied = true };
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IPublicAuthRequestApiClient>(api)
            .AddAuthenticated<IAuthRequestApiClient>(api);
        var service = new TrustedDeviceApprovalService(
            provider,
            _crypto,
            new FingerprintService(),
            session);
        var request = await service.BeginAsync(
            lease,
            "me@example.com",
            DeviceIdentifier,
            TestContext.Current.CancellationToken);

        var result = await service.PollAsync(
            lease,
            request.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(TrustedDeviceApprovalStatus.Denied, result.Status);
        await Assert.ThrowsAsync<TrustedDeviceApprovalException>(() =>
            service.PollAsync(
                lease,
                request.Id,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Poll_MalformedLegacyRsaMacClearsPendingRequest()
    {
        var session = CreateLockedAuthenticatedSession();
        var lease = session.CaptureLease();
        var api = new FakeApprovalApi();
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IPublicAuthRequestApiClient>(api)
            .AddAuthenticated<IAuthRequestApiClient>(api);
        var service = new TrustedDeviceApprovalService(
            provider,
            _crypto,
            new FingerprintService(),
            session);
        var request = await service.BeginAsync(
            lease,
            "me@example.com",
            DeviceIdentifier,
            TestContext.Current.CancellationToken);
        api.ApprovalKey = $"5.{Convert.ToBase64String(new byte[256])}|AA==";

        await Assert.ThrowsAsync<TrustedDeviceApprovalException>(() =>
            service.PollAsync(
                lease,
                request.Id,
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TrustedDeviceApprovalException>(() =>
            service.PollAsync(
                lease,
                request.Id,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Poll_MalformedV2TransportClearsDecryptedBuffer()
    {
        var session = CreateLockedAuthenticatedSession();
        var lease = session.CaptureLease();
        var api = new FakeApprovalApi { ApprovalKey = "4.AA==" };
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IPublicAuthRequestApiClient>(api)
            .AddAuthenticated<IAuthRequestApiClient>(api);
        var plaintext = Enumerable.Repeat((byte)0xA5, 31).ToArray();
        var service = new TrustedDeviceApprovalService(
            provider,
            _crypto,
            new FingerprintService(),
            session,
            timeProvider: null,
            store: null,
            (_, _) => plaintext);
        var request = await service.BeginAsync(
            lease,
            "me@example.com",
            DeviceIdentifier,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<TrustedDeviceApprovalException>(() =>
            service.PollAsync(
                lease,
                request.Id,
                TestContext.Current.CancellationToken));

        Assert.All(plaintext, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Begin_AdministratorApproval_UsesAuthenticatedTypeTwoAndSevenDayLifetime()
    {
        var now = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        var session = CreateLockedAuthenticatedSession();
        var lease = session.CaptureLease();
        var api = new FakeApprovalApi
        {
            ResponseId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        };
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IPublicAuthRequestApiClient>(api)
            .AddAuthenticated<IAuthRequestApiClient>(api);
        var service = new TrustedDeviceApprovalService(
            provider,
            _crypto,
            new FingerprintService(),
            session,
            time,
            new FakeApprovalStore());

        var request = await service.BeginAsync(
            lease,
            "me@example.com",
            DeviceIdentifier,
            TrustedDeviceApprovalMethod.Administrator,
            TestContext.Current.CancellationToken);

        Assert.Null(api.Created);
        Assert.NotNull(api.CreatedAdmin);
        Assert.Equal(2, api.CreatedAdmin!.Type);
        Assert.Equal(now + TimeSpan.FromDays(7), request.ExpiresAt);
    }

    [Fact]
    public async Task Begin_AdministratorApprovalWithoutDurableStoreFailsBeforeApiCall()
    {
        var session = CreateLockedAuthenticatedSession();
        var lease = session.CaptureLease();
        var api = new FakeApprovalApi();
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IPublicAuthRequestApiClient>(api)
            .AddAuthenticated<IAuthRequestApiClient>(api);
        var service = new TrustedDeviceApprovalService(
            provider,
            _crypto,
            new FingerprintService(),
            session);

        await Assert.ThrowsAsync<TrustedDeviceApprovalException>(() =>
            service.BeginAsync(
                lease,
                "me@example.com",
                DeviceIdentifier,
                TrustedDeviceApprovalMethod.Administrator,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, api.AdminCreateCalls);
    }

    [Fact]
    public async Task Begin_AdministratorCancellationAfterResponseStillPersistsRecoveryRecord()
    {
        var session = CreateLockedAuthenticatedSession();
        var lease = session.CaptureLease();
        using var cancellation = new CancellationTokenSource();
        var api = new FakeApprovalApi
        {
            ResponseId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            OnAdminCreated = cancellation.Cancel,
        };
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IPublicAuthRequestApiClient>(api)
            .AddAuthenticated<IAuthRequestApiClient>(api);
        var store = new FakeApprovalStore();
        var service = new TrustedDeviceApprovalService(
            provider,
            _crypto,
            new FingerprintService(),
            session,
            store: store);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.BeginAsync(
                lease,
                "me@example.com",
                DeviceIdentifier,
                TrustedDeviceApprovalMethod.Administrator,
                cancellation.Token));

        using var read = store.Read(new DeviceKeyBinding(
            lease.AccountId,
            lease.Endpoints,
            DeviceIdentifier));
        Assert.Equal(TrustedDeviceApprovalStoreReadStatus.Found, read.Status);
        Assert.Equal(api.ResponseId, read.Record!.Pending.RequestId);
    }

    [Fact]
    public async Task AdministratorApproval_RestoresDurableRequestAndCancelOnlyStopsPolling()
    {
        var now = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var session = CreateLockedAuthenticatedSession();
        var lease = session.CaptureLease();
        var api = new FakeApprovalApi();
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IPublicAuthRequestApiClient>(api)
            .AddAuthenticated<IAuthRequestApiClient>(api);
        var store = new FakeApprovalStore();
        using (var rsa = RSA.Create(2048))
        {
            var privateKey = rsa.ExportPkcs8PrivateKey();
            try
            {
                using var pending = TrustedDeviceApprovalPending.CreateWithDefaultLifetime(
                    "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                    privateKey,
                    lease.AccountId,
                    lease.Endpoints,
                    DeviceIdentifier,
                    now,
                    TrustedDeviceApprovalRequestType.AdminApproval);
                store.Seed(pending);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }
        var service = new TrustedDeviceApprovalService(
            provider,
            _crypto,
            new FingerprintService(),
            session,
            new ManualTimeProvider(now),
            store);

        var first = await service.BeginAsync(
            lease,
            "me@example.com",
            DeviceIdentifier,
            TrustedDeviceApprovalMethod.Administrator,
            TestContext.Current.CancellationToken);
        service.Cancel(lease);
        var second = await service.BeginAsync(
            lease,
            "me@example.com",
            DeviceIdentifier,
            TrustedDeviceApprovalMethod.Administrator,
            TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.FingerprintPhrase, second.FingerprintPhrase);
        Assert.Equal(0, api.AdminCreateCalls);
        Assert.Equal(0, store.RemoveCalls);

        service.Abandon(lease);
        Assert.Equal(1, store.RemoveCalls);
        Assert.Equal(TrustedDeviceApprovalRemovalReason.Cancelled, store.LastRemovalReason);
    }

    [Fact]
    public async Task AdministratorApproval_DoesNotExpireFromClientClockAlone()
    {
        var now = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        var session = CreateLockedAuthenticatedSession();
        var lease = session.CaptureLease();
        var api = new FakeApprovalApi
        {
            ResponseId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        };
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IPublicAuthRequestApiClient>(api)
            .AddAuthenticated<IAuthRequestApiClient>(api);
        var store = new FakeApprovalStore();
        var service = new TrustedDeviceApprovalService(
            provider,
            _crypto,
            new FingerprintService(),
            session,
            time,
            store);
        var request = await service.BeginAsync(
            lease,
            "me@example.com",
            DeviceIdentifier,
            TrustedDeviceApprovalMethod.Administrator,
            TestContext.Current.CancellationToken);
        time.UtcNow = now + TimeSpan.FromDays(8);

        var poll = await service.PollAsync(
            lease,
            request.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(TrustedDeviceApprovalStatus.Pending, poll.Status);
        Assert.Equal(1, api.GetCalls);
        Assert.Equal(0, store.RemoveCalls);
    }

    [Fact]
    public async Task AdministratorApproval_ServerNotFoundRemovesDurableRequest()
    {
        var session = CreateLockedAuthenticatedSession();
        var lease = session.CaptureLease();
        var api = new FakeApprovalApi
        {
            ResponseId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            GetException = new HttpRequestException(
                "expired",
                inner: null,
                System.Net.HttpStatusCode.NotFound),
        };
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IPublicAuthRequestApiClient>(api)
            .AddAuthenticated<IAuthRequestApiClient>(api);
        var store = new FakeApprovalStore();
        var service = new TrustedDeviceApprovalService(
            provider,
            _crypto,
            new FingerprintService(),
            session,
            store: store);
        var request = await service.BeginAsync(
            lease,
            "me@example.com",
            DeviceIdentifier,
            TrustedDeviceApprovalMethod.Administrator,
            TestContext.Current.CancellationToken);

        var poll = await service.PollAsync(
            lease,
            request.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(TrustedDeviceApprovalStatus.Expired, poll.Status);
        Assert.Equal(1, store.RemoveCalls);
        Assert.Equal(TrustedDeviceApprovalRemovalReason.Expired, store.LastRemovalReason);
    }

    [Fact]
    public async Task AdministratorApproval_CancelWinsPollRaceWithoutDeletingDurableRequest()
    {
        var now = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var session = CreateLockedAuthenticatedSession();
        var lease = session.CaptureLease();
        var api = new FakeApprovalApi
        {
            ResponseId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            GetEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueGet = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IPublicAuthRequestApiClient>(api)
            .AddAuthenticated<IAuthRequestApiClient>(api);
        var store = new FakeApprovalStore();
        var service = new TrustedDeviceApprovalService(
            provider,
            _crypto,
            new FingerprintService(),
            session,
            new ManualTimeProvider(now),
            store);
        var request = await service.BeginAsync(
            lease,
            "me@example.com",
            DeviceIdentifier,
            TrustedDeviceApprovalMethod.Administrator,
            TestContext.Current.CancellationToken);

        var poll = service.PollAsync(
            lease,
            request.Id,
            TestContext.Current.CancellationToken);
        await api.GetEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        service.Cancel(lease);
        api.ContinueGet.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
        Assert.Equal(0, store.RemoveCalls);
        var restored = await service.BeginAsync(
            lease,
            "me@example.com",
            DeviceIdentifier,
            TrustedDeviceApprovalMethod.Administrator,
            TestContext.Current.CancellationToken);
        Assert.Equal(request.Id, restored.Id);
        Assert.Equal(1, api.AdminCreateCalls);
    }

    private static EncString EncryptForType(
        byte[] plaintext,
        byte[] publicKey,
        EncryptionType type)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
        Assert.Equal(publicKey.Length, bytesRead);
        var padding = type is
            EncryptionType.Rsa2048_OaepSha256_B64
                or EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64
            ? RSAEncryptionPadding.OaepSHA256
            : RSAEncryptionPadding.OaepSHA1;
        return new EncString(
            type,
            [],
            rsa.Encrypt(plaintext, padding),
            type.HasLegacyRsaMac()
                ? new byte[EncString.LegacyRsaMacLength]
                : null);
    }

    private static VaultSession CreateLockedAuthenticatedSession()
    {
        var session = new VaultSession();
        session.BeginAccountSession(
            "me@example.com",
            "me@example.com",
            ServerEndpoints.FromWebVault("https://vault.example"));
        session.SetTokens("access-token", "refresh-token");
        session.SetState(Core.Session.VaultState.Locked);
        return session;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FakeApprovalApi :
        IPublicAuthRequestApiClient,
        IAuthRequestApiClient
    {
        public AuthRequestRequest? Created { get; private set; }
        public AuthRequestRequest? CreatedAdmin { get; private set; }
        public int AdminCreateCalls { get; private set; }
        public string ResponseId { get; init; } = "request-id";
        public TaskCompletionSource? GetEntered { get; init; }
        public TaskCompletionSource? ContinueGet { get; init; }
        public string? ApprovalKey { get; set; }
        public bool Denied { get; set; }
        public Action? OnAdminCreated { get; init; }
        public Exception? GetException { get; init; }
        public int GetCalls { get; private set; }

        public Task<AuthRequestResponse> CreateAsync(
            AuthRequestRequest request,
            CancellationToken ct = default)
        {
            Created = request;
            return Task.FromResult(Response(null));
        }

        public Task<AuthRequestResponse> GetResponseAsync(
            string id,
            string code,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AuthRequestResponse> CreateAdminAsync(
            AuthRequestRequest request,
            CancellationToken ct = default)
        {
            AdminCreateCalls++;
            CreatedAdmin = request;
            Created = null;
            OnAdminCreated?.Invoke();
            return Task.FromResult(Response(null, request.PublicKey));
        }

        public async Task<AuthRequestResponse> GetAsync(
            string id,
            CancellationToken ct = default)
        {
            GetCalls++;
            if (GetException is not null)
                throw GetException;
            GetEntered?.TrySetResult();
            if (ContinueGet is not null)
                await ContinueGet.Task;
            return Response(Denied ? false : ApprovalKey is null ? null : true);
        }

        public Task<AuthRequestListResponse> GetPendingAsync(
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AuthRequestResponse> ApproveAsync(
            string id,
            AuthResponseRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        private AuthRequestResponse Response(bool? approved, string? publicKey = null) => new(
            Id: ResponseId,
            PublicKey: publicKey ?? Created?.PublicKey ?? CreatedAdmin?.PublicKey ?? string.Empty,
            RequestDeviceType: "Windows",
            RequestIpAddress: "127.0.0.1",
            Key: ApprovalKey,
            MasterPasswordHash: null,
            CreationDate: DateTimeOffset.UtcNow.ToString("O"),
            ResponseDate: approved is null ? null : DateTimeOffset.UtcNow.ToString("O"),
            RequestApproved: approved,
            Origin: null);
    }

    private sealed class FakeApprovalStore : ITrustedDeviceApprovalStore
    {
        private readonly object _gate = new();
        private TrustedDeviceApprovalPending? _pending;
        private bool _intentHeld;

        public int RemoveCalls { get; private set; }
        public TrustedDeviceApprovalRemovalReason? LastRemovalReason { get; private set; }

        public void Seed(TrustedDeviceApprovalPending pending)
        {
            _pending?.Dispose();
            _pending = pending.Clone();
        }

        public TrustedDeviceApprovalStoreReadResult Read(DeviceKeyBinding binding) =>
            _pending is not null && _pending.Binding == binding
                ? TrustedDeviceApprovalStoreReadResult.Found(CreateRecord())
                : TrustedDeviceApprovalStoreReadResult.Absent;

        public TrustedDeviceApprovalStoreReadResult Read(
            DeviceKeyBinding binding,
            string requestId) =>
            _pending is not null
                && _pending.Binding == binding
                && _pending.RequestId == requestId
                ? TrustedDeviceApprovalStoreReadResult.Found(CreateRecord())
                : TrustedDeviceApprovalStoreReadResult.Absent;

        public ITrustedDeviceApprovalIntentLease? TryAcquireBeginIntent(
            DeviceKeyBinding binding)
        {
            lock (_gate)
            {
                if (_pending is not null || _intentHeld)
                    return null;
                _intentHeld = true;
                return new FakeIntent(this, binding);
            }
        }

        public TrustedDeviceApprovalStoreRecord? TryCreate(
            ITrustedDeviceApprovalIntentLease intent,
            TrustedDeviceApprovalPending pending)
        {
            lock (_gate)
            {
                if (intent is not FakeIntent candidate
                    || !candidate.IsActiveFor(this, pending.Binding))
                {
                    throw new ArgumentException("The fake approval intent is invalid.", nameof(intent));
                }
                if (_pending is not null)
                    return null;
                Seed(pending);
                return CreateRecord();
            }
        }

        public bool Remove(
            TrustedDeviceApprovalStoreRecord expected,
            TrustedDeviceApprovalRemovalReason reason)
        {
            if (_pending is null
                || expected.Pending.RequestId != _pending.RequestId
                || expected.Pending.Binding != _pending.Binding)
            {
                return false;
            }

            RemoveCalls++;
            LastRemovalReason = reason;
            _pending.Dispose();
            _pending = null;
            expected.Dispose();
            return true;
        }

        private TrustedDeviceApprovalStoreRecord CreateRecord() =>
            new(Guid.NewGuid(), 1, _pending!.Clone());

        private void ReleaseIntent()
        {
            lock (_gate)
                _intentHeld = false;
        }

        private sealed class FakeIntent(
            FakeApprovalStore owner,
            DeviceKeyBinding binding) : ITrustedDeviceApprovalIntentLease
        {
            private FakeApprovalStore? _owner = owner;

            public DeviceKeyBinding Binding { get; } = binding;

            public bool IsActiveFor(
                FakeApprovalStore candidateOwner,
                DeviceKeyBinding candidateBinding) =>
                ReferenceEquals(_owner, candidateOwner)
                && Binding == candidateBinding;

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.ReleaseIntent();
            }
        }
    }
}
