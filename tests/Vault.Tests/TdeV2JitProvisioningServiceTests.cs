using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

public sealed class TdeV2JitProvisioningServiceTests
{
    private const string AccountId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string OrganizationId = "11111111-2222-3333-4444-555555555555";
    private const string DeviceId = "99999999-8888-7777-6666-555555555555";
    private static readonly ServerEndpoints Endpoints =
        ServerEndpoints.FromWebVault("https://vault.example");

    [Fact]
    public async Task Provision_UsesOfficialOrderAndCommitsV2Session()
    {
        using var organizationRsa = RSA.Create(2048);
        var organizationPublicKey = organizationRsa.ExportSubjectPublicKeyInfo();
        var fixture = CreateFixture(organizationPublicKey, featureEnabled: true);

        var result = await fixture.Service.ProvisionAsync(
            fixture.Lease,
            "enterprise-sso",
            trustCurrentDevice: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["autoEnroll", "organizationKeys", "config", "ledger:create", "ledger:AccountKeysSubmissionStarted", "accountKeys", "ledger:AccountKeysConfirmed", "resetEnrollment", "ledger:ResetPasswordEnrolled", "deviceKeys", "ledger:TrustedDeviceEnrolled", "ledger:LocalCommitReady", "ledger:remove", "sync", "config", "devices"],
            fixture.Api.Operations);
        Assert.Equal("mldsa44", fixture.Api.PostedKeys!.AccountKeys.SignatureKeyPair.SignatureAlgorithm);
        Assert.Matches("^[0-9a-f]{32}$", fixture.Api.PostedKeys.UserKeyId);
        Assert.Null(fixture.Api.ResetEnrollment!.MasterPasswordHash);
        Assert.StartsWith("4.", fixture.Api.DeviceKeys!.EncryptedUserKey);
        Assert.StartsWith("7.", fixture.Api.DeviceKeys.EncryptedPublicKey);
        Assert.StartsWith("2.", fixture.Api.DeviceKeys.EncryptedPrivateKey);
        Assert.True(fixture.Session.UsesV2UserKey);
        Assert.Equal(2, fixture.Session.SecurityVersion);
        Assert.Equal(Core.Session.VaultState.Unlocked, fixture.Session.State);
        Assert.Equal(fixture.Api.PostedKeys.EncryptedPrivateKey, fixture.Session.EncryptedPrivateKey);
        Assert.True(fixture.DeviceKeys.Contains(
            new DeviceKeyBinding(AccountId, Endpoints, DeviceId)));
        Assert.True(fixture.DeviceTrust.CapabilitySupported);
        Assert.True(result.CurrentDeviceTrusted);
        Assert.Equal(fixture.Api.PostedKeys.UserKeyId, result.UserKeyId);
        CryptographicOperations.ZeroMemory(organizationPublicKey);
    }

    [Fact]
    public async Task Provision_FeatureMissingStopsBeforeAnyWrite()
    {
        using var organizationRsa = RSA.Create(2048);
        var organizationPublicKey = organizationRsa.ExportSubjectPublicKeyInfo();
        var fixture = CreateFixture(organizationPublicKey, featureEnabled: false);

        var exception = await Assert.ThrowsAsync<TdeV2JitProvisioningException>(() =>
            fixture.Service.ProvisionAsync(
                fixture.Lease,
                "enterprise-sso",
                trustCurrentDevice: true,
                TestContext.Current.CancellationToken));

        Assert.Equal(TdeV2JitProvisioningStage.CapabilityCheck, exception.Stage);
        Assert.Equal(["autoEnroll", "organizationKeys", "config"], fixture.Api.Operations);
        Assert.Null(fixture.Api.PostedKeys);
        Assert.False(fixture.Session.UsesV2UserKey);
        CryptographicOperations.ZeroMemory(organizationPublicKey);
    }

    [Fact]
    public async Task SsoWithoutTrustedDeviceOption_DoesNotStartJitOrWritePendingState()
    {
        using var organizationRsa = RSA.Create(2048);
        var organizationPublicKey = organizationRsa.ExportSubjectPublicKeyInfo();
        try
        {
            var fixture = CreateFixture(organizationPublicKey, featureEnabled: true);
            var restarted = CreateRestartedAuth(
                fixture.Api,
                fixture.Pending,
                UnprovisionedToken(trustedDeviceOption: null));

            var result = await restarted.Auth.LoginWithSsoAsync(
                restarted.Auth.BeginAuthenticationAttempt(),
                Endpoints.WebVault.Value,
                "me@example.com",
                SsoAuthorization("enterprise-sso"),
                TestContext.Current.CancellationToken);

            var failure = Assert.IsType<AuthResult.Failure>(result);
            Assert.Equal(AuthFailureKind.UnsupportedSsoDecryption, failure.Kind);
            Assert.Empty(fixture.Api.Operations);
            Assert.Null(fixture.Api.PostedKeys);
            Assert.Null(fixture.Pending.Stage);
            Assert.Equal(Core.Session.VaultState.LoggedOut, restarted.Session.State);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(organizationPublicKey);
        }
    }

    [Fact]
    public async Task SsoWithoutTrustedDeviceOption_DoesNotResumeExistingJitPendingState()
    {
        using var organizationRsa = RSA.Create(2048);
        var organizationPublicKey = organizationRsa.ExportSubjectPublicKeyInfo();
        try
        {
            var fixture = CreateFixture(organizationPublicKey, featureEnabled: true);
            fixture.Api.AccountKeysFailure = new HttpRequestException("response lost");
            await Assert.ThrowsAsync<TdeV2JitProvisioningException>(() =>
                fixture.Service.ProvisionAsync(
                    fixture.Lease,
                    "enterprise-sso",
                    trustCurrentDevice: true,
                    TestContext.Current.CancellationToken));
            var pendingStage = fixture.Pending.Stage;
            var postedKeys = fixture.Api.PostedKeys;
            Assert.Equal(TdeV2JitPendingStage.AccountKeysSubmissionStarted, pendingStage);
            Assert.NotNull(postedKeys);
            fixture.Api.Operations.Clear();

            var restarted = CreateRestartedAuth(
                fixture.Api,
                fixture.Pending,
                UnprovisionedToken(trustedDeviceOption: null));
            var result = await restarted.Auth.LoginWithSsoAsync(
                restarted.Auth.BeginAuthenticationAttempt(),
                Endpoints.WebVault.Value,
                "me@example.com",
                SsoAuthorization("enterprise-sso"),
                TestContext.Current.CancellationToken);

            var failure = Assert.IsType<AuthResult.Failure>(result);
            Assert.Equal(AuthFailureKind.UnsupportedSsoDecryption, failure.Kind);
            Assert.Empty(fixture.Api.Operations);
            Assert.Same(postedKeys, fixture.Api.PostedKeys);
            Assert.Equal(pendingStage, fixture.Pending.Stage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(organizationPublicKey);
        }
    }

    [Fact]
    public async Task SsoWithActiveTrustedDeviceOption_PreservesJitCandidateBehavior()
    {
        using var organizationRsa = RSA.Create(2048);
        var organizationPublicKey = organizationRsa.ExportSubjectPublicKeyInfo();
        try
        {
            var fixture = CreateFixture(organizationPublicKey, featureEnabled: true);
            var restarted = CreateRestartedAuth(
                fixture.Api,
                fixture.Pending,
                UnprovisionedToken(TrustedDeviceOption(isTdeOffboarding: false)));

            var result = await restarted.Auth.LoginWithSsoAsync(
                restarted.Auth.BeginAuthenticationAttempt(),
                Endpoints.WebVault.Value,
                "me@example.com",
                SsoAuthorization("enterprise-sso"),
                TestContext.Current.CancellationToken);

            Assert.IsType<AuthResult.TdeV2JitProvisioningRequired>(result);
            Assert.Equal(["config"], fixture.Api.Operations);
            Assert.Null(fixture.Api.PostedKeys);
            Assert.Null(fixture.Pending.Stage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(organizationPublicKey);
        }
    }

    [Fact]
    public async Task SsoWithOffboardingOption_PreservesExistingRejectionWithoutJitWrites()
    {
        using var organizationRsa = RSA.Create(2048);
        var organizationPublicKey = organizationRsa.ExportSubjectPublicKeyInfo();
        try
        {
            var fixture = CreateFixture(organizationPublicKey, featureEnabled: true);
            var restarted = CreateRestartedAuth(
                fixture.Api,
                fixture.Pending,
                UnprovisionedToken(TrustedDeviceOption(isTdeOffboarding: true)));

            var result = await restarted.Auth.LoginWithSsoAsync(
                restarted.Auth.BeginAuthenticationAttempt(),
                Endpoints.WebVault.Value,
                "me@example.com",
                SsoAuthorization("enterprise-sso"),
                TestContext.Current.CancellationToken);

            var failure = Assert.IsType<AuthResult.Failure>(result);
            Assert.Equal(AuthFailureKind.UnsupportedSsoDecryption, failure.Kind);
            Assert.Contains("offboarding", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(fixture.Api.Operations);
            Assert.Null(fixture.Api.PostedKeys);
            Assert.Null(fixture.Pending.Stage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(organizationPublicKey);
        }
    }

    [Fact]
    public async Task Provision_AccountKeysResponseLostMarksOutcomeUncertainAndDoesNotContinue()
    {
        using var organizationRsa = RSA.Create(2048);
        var organizationPublicKey = organizationRsa.ExportSubjectPublicKeyInfo();
        var fixture = CreateFixture(organizationPublicKey, featureEnabled: true);
        fixture.Api.AccountKeysFailure = new HttpRequestException("response lost");

        var exception = await Assert.ThrowsAsync<TdeV2JitProvisioningException>(() =>
            fixture.Service.ProvisionAsync(
                fixture.Lease,
                "enterprise-sso",
                trustCurrentDevice: true,
                TestContext.Current.CancellationToken));

        Assert.Equal(TdeV2JitProvisioningStage.AccountKeys, exception.Stage);
        Assert.True(exception.AccountKeysMayHaveBeenCommitted);
        Assert.Equal(
            ["autoEnroll", "organizationKeys", "config", "ledger:create", "ledger:AccountKeysSubmissionStarted", "accountKeys"],
            fixture.Api.Operations);
        Assert.Null(fixture.Api.ResetEnrollment);
        Assert.Null(fixture.Api.DeviceKeys);
        Assert.Equal(TdeV2JitPendingStage.AccountKeysSubmissionStarted, fixture.Pending.Stage);
        CryptographicOperations.ZeroMemory(organizationPublicKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResponseLost_RestartUsesTokenEvidenceAndResumesExactMaterial(
        bool trustCurrentDevice)
    {
        using var organizationRsa = RSA.Create(2048);
        var organizationPublicKey = organizationRsa.ExportSubjectPublicKeyInfo();
        var fixture = CreateFixture(organizationPublicKey, featureEnabled: true);
        fixture.Api.AccountKeysFailure = new HttpRequestException("response lost");

        await Assert.ThrowsAsync<TdeV2JitProvisioningException>(() =>
            fixture.Service.ProvisionAsync(
                fixture.Lease,
                "enterprise-sso",
                trustCurrentDevice,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            TdeV2JitPendingStage.AccountKeysSubmissionStarted,
            fixture.Pending.Stage);

        fixture.Api.AccountKeysFailure = null;
        fixture.Api.Operations.Clear();
        var restarted = CreateRestartedAuth(
            fixture.Api,
            fixture.Pending,
            TokenWithAccountKeys(fixture.Api.PostedKeys!.AccountKeys));
        var authenticated = await restarted.Auth.LoginWithSsoAsync(
            restarted.Auth.BeginAuthenticationAttempt(),
            Endpoints.WebVault.Value,
            "me@example.com",
            SsoAuthorization("enterprise-sso"),
            TestContext.Current.CancellationToken);

        var failure = authenticated as AuthResult.Failure;
        Assert.False(failure is not null, failure?.Message);
        var pending = Assert.IsType<AuthResult.TdeV2JitProvisioningRequired>(authenticated);
        Assert.Equal(trustCurrentDevice, pending.RequiredTrustCurrentDevice);
        Assert.Equal(["ledger:AccountKeysConfirmed"], fixture.Api.Operations);

        var completed = await restarted.Auth.CompleteTdeV2JitProvisioningAsync(
            pending.Lease,
            pending.OrganizationIdentifier,
            trustCurrentDevice: !trustCurrentDevice,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(completed);
        Assert.True(restarted.Session.UsesV2UserKey);
        Assert.DoesNotContain("accountKeys", fixture.Api.Operations);
        Assert.DoesNotContain("autoEnroll", fixture.Api.Operations);
        Assert.DoesNotContain("organizationKeys", fixture.Api.Operations);
        Assert.DoesNotContain("config", fixture.Api.Operations.TakeWhile(x => x != "sync"));
        Assert.Equal(trustCurrentDevice, fixture.Api.DeviceKeys is not null);
        Assert.Null(fixture.Pending.Stage);
        CryptographicOperations.ZeroMemory(organizationPublicKey);
    }

    [Fact]
    public async Task ConfirmedPending_WithMissingServerAccountKeysFailsClosedWithoutWrites()
    {
        using var organizationRsa = RSA.Create(2048);
        var organizationPublicKey = organizationRsa.ExportSubjectPublicKeyInfo();
        var fixture = CreateFixture(organizationPublicKey, featureEnabled: true);
        fixture.Api.AccountKeysFailure = new HttpRequestException("response lost");
        await Assert.ThrowsAsync<TdeV2JitProvisioningException>(() =>
            fixture.Service.ProvisionAsync(
                fixture.Lease,
                "enterprise-sso",
                trustCurrentDevice: true,
                TestContext.Current.CancellationToken));
        fixture.Pending.AdvanceForTest(TdeV2JitPendingStage.AccountKeysConfirmed);
        fixture.Api.Operations.Clear();
        var token = TokenWithAccountKeys(fixture.Api.PostedKeys!.AccountKeys) with
        {
            AccountKeys = null,
        };
        var restarted = CreateRestartedAuth(fixture.Api, fixture.Pending, token);

        var result = await restarted.Auth.LoginWithSsoAsync(
            restarted.Auth.BeginAuthenticationAttempt(),
            Endpoints.WebVault.Value,
            "me@example.com",
            SsoAuthorization("enterprise-sso"),
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.SafeMessage, failure.Kind);
        Assert.Empty(fixture.Api.Operations);
        Assert.Equal(TdeV2JitPendingStage.AccountKeysConfirmed, fixture.Pending.Stage);
        CryptographicOperations.ZeroMemory(organizationPublicKey);
    }

    [Fact]
    public async Task PendingEnrollment_FromDifferentSsoOrganizationFailsClosedWithoutWrites()
    {
        using var organizationRsa = RSA.Create(2048);
        var organizationPublicKey = organizationRsa.ExportSubjectPublicKeyInfo();
        var fixture = CreateFixture(organizationPublicKey, featureEnabled: true);
        fixture.Api.AccountKeysFailure = new HttpRequestException("response lost");
        await Assert.ThrowsAsync<TdeV2JitProvisioningException>(() =>
            fixture.Service.ProvisionAsync(
                fixture.Lease,
                "enterprise-sso",
                trustCurrentDevice: true,
                TestContext.Current.CancellationToken));
        fixture.Api.Operations.Clear();
        var restarted = CreateRestartedAuth(
            fixture.Api,
            fixture.Pending,
            TokenWithAccountKeys(fixture.Api.PostedKeys!.AccountKeys));

        var result = await restarted.Auth.LoginWithSsoAsync(
            restarted.Auth.BeginAuthenticationAttempt(),
            Endpoints.WebVault.Value,
            "me@example.com",
            SsoAuthorization("different-enterprise"),
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.SafeMessage, failure.Kind);
        Assert.Empty(fixture.Api.Operations);
        Assert.Equal(
            TdeV2JitPendingStage.AccountKeysSubmissionStarted,
            fixture.Pending.Stage);
        CryptographicOperations.ZeroMemory(organizationPublicKey);
    }

    private static Fixture CreateFixture(byte[] organizationPublicKey, bool featureEnabled)
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession(AccountId, "me@example.com", Endpoints);
        session.SetTokens("access-token", "refresh-token");
        session.SetState(Core.Session.VaultState.Locked);
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            AccountId,
            Endpoints,
            lease.Generation,
            "me@example.com",
            DeviceId,
            "refresh-token",
            string.Empty,
            KdfType.Pbkdf2,
            600_000,
            null,
            null,
            "me@example.com",
            PersistedSessionUnlockMethod.TrustedDeviceSso));
        var api = new FakeJitApi(organizationPublicKey, featureEnabled, AccountId, DeviceId);
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IServerConfigApiClient>(api)
            .AddAuthenticated<ITdeRegistrationApiClient>(api)
            .AddAuthenticated<IDeviceTrustApiClient>(api)
            .AddAuthenticated<IReadonlyApiClient>(api)
            .AddAuthenticated<IServerConfigApiClient>(api);
        var decryptor = new VaultDecryptor(new CryptoService());
        var cache = new VaultCacheService(new FakeCacheStore(), decryptor, session);
        var sync = new SyncService(provider, decryptor, session, cache, tokenStore);
        var bootstrapper = new VaultBootstrapper(sync, provider, session);
        var deviceKeys = new FakeDeviceKeyStore();
        var deviceTrust = new FakeDeviceTrustService();
        var pending = new FakeTdeV2JitPendingStore(api.Operations);
        var service = new TdeV2JitProvisioningService(
            provider,
            deviceKeys,
            tokenStore,
            session,
            bootstrapper,
            deviceTrust,
            pending);
        return new Fixture(
            service,
            api,
            session,
            lease,
            deviceKeys,
            deviceTrust,
            pending);
    }

    private static RestartedAuthFixture CreateRestartedAuth(
        FakeJitApi api,
        FakeTdeV2JitPendingStore pending,
        TokenResponse token)
    {
        var session = new VaultSession();
        var tokenStore = new MemoryTokenStore();
        var identity = new ControlledIdentityApiClient(_ =>
            Task.FromResult<ConnectTokenResult>(new ConnectTokenResult.Success(token)));
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IIdentityApiClient>(identity)
            .AddAnonymous<IServerConfigApiClient>(api)
            .AddAuthenticated<ITdeRegistrationApiClient>(api)
            .AddAuthenticated<IDeviceTrustApiClient>(api)
            .AddAuthenticated<IReadonlyApiClient>(api)
            .AddAuthenticated<IServerConfigApiClient>(api);
        var decryptor = new VaultDecryptor(new CryptoService());
        var cache = new VaultCacheService(new FakeCacheStore(), decryptor, session);
        var sync = new SyncService(provider, decryptor, session, cache, tokenStore);
        var bootstrapper = new VaultBootstrapper(sync, provider, session);
        var refresher = new TokenRefreshService(provider, session, tokenStore);
        var deviceTrust = new FakeDeviceTrustService();
        var jit = new TdeV2JitProvisioningService(
            provider,
            new FakeDeviceKeyStore(),
            tokenStore,
            session,
            bootstrapper,
            deviceTrust,
            pending);
        var auth = new AuthService(
            provider,
            new CryptoService(),
            session,
            tokenStore,
            bootstrapper,
            cache,
            refresher,
            deviceTrust: deviceTrust,
            deviceIdentifierProvider: new FixedDeviceIdentifierProvider(DeviceId),
            tdeV2JitProvisioning: jit,
            tdeV2JitPendingStore: pending);
        return new RestartedAuthFixture(auth, session);
    }

    private static TokenResponse TokenWithAccountKeys(AccountKeysRequest request) => new(
        AccessToken(),
        "restart-refresh",
        3600,
        "Bearer",
        "api offline_access",
        Key: null,
        PrivateKey: null,
        KdfType.Pbkdf2,
        600_000,
        UserDecryptionOptions: new UserDecryptionOptionsResponse(
            HasMasterPassword: false,
            TrustedDeviceOption: new TrustedDeviceUserDecryptionOptionResponse(
                HasAdminApproval: false,
                HasLoginApprovingDevice: false,
                HasManageResetPasswordPermission: false,
                IsTdeOffboarding: false)),
        AccountKeys: new AccountKeysResponse(
            new PublicKeyEncryptionKeyPairResponse(
                request.PublicKeyEncryptionKeyPair.WrappedPrivateKey,
                request.PublicKeyEncryptionKeyPair.PublicKey,
                request.PublicKeyEncryptionKeyPair.SignedPublicKey),
            new SignatureKeyPairResponse(
                request.SignatureKeyPair.WrappedSigningKey,
                request.SignatureKeyPair.VerifyingKey),
            request.SecurityState));

    private static TokenResponse UnprovisionedToken(
        TrustedDeviceUserDecryptionOptionResponse? trustedDeviceOption) => new(
        AccessToken(),
        "restart-refresh",
        3600,
        "Bearer",
        "api offline_access",
        Key: null,
        PrivateKey: null,
        KdfType.Pbkdf2,
        600_000,
        UserDecryptionOptions: new UserDecryptionOptionsResponse(
            HasMasterPassword: false,
            TrustedDeviceOption: trustedDeviceOption));

    private static TrustedDeviceUserDecryptionOptionResponse TrustedDeviceOption(
        bool isTdeOffboarding) => new(
        HasAdminApproval: false,
        HasLoginApprovingDevice: false,
        HasManageResetPasswordPermission: false,
        IsTdeOffboarding: isTdeOffboarding);

    private static string AccessToken()
    {
        static string Base64Url(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        return $"{Base64Url("{\"alg\":\"none\"}")}.{Base64Url($"{{\"sub\":\"{AccountId}\",\"email\":\"me@example.com\"}}")}.signature";
    }

    private static SsoAuthorizationCode SsoAuthorization(string organizationIdentifier) => new(
        "sso-code",
        new string('v', 43),
        "http://localhost:8065",
        new string('s', 43),
        organizationIdentifier);

    private sealed record Fixture(
        TdeV2JitProvisioningService Service,
        FakeJitApi Api,
        VaultSession Session,
        Core.Session.VaultSessionLease Lease,
        FakeDeviceKeyStore DeviceKeys,
        FakeDeviceTrustService DeviceTrust,
        FakeTdeV2JitPendingStore Pending);

    private sealed record RestartedAuthFixture(
        AuthService Auth,
        VaultSession Session);

    private sealed class FakeJitApi(
        byte[] organizationPublicKey,
        bool featureEnabled,
        string accountId,
        string deviceIdentifier) :
        IServerConfigApiClient,
        ITdeRegistrationApiClient,
        IDeviceTrustApiClient,
        IReadonlyApiClient
    {
        public List<string> Operations { get; } = [];
        public Exception? AccountKeysFailure { get; set; }
        public PostAccountKeysRequest? PostedKeys { get; private set; }
        public ResetPasswordEnrollmentRequest? ResetEnrollment { get; private set; }
        public TrustedDeviceKeysRequest? DeviceKeys { get; private set; }

        public Task<OrganizationAutoEnrollStatusResponse> GetAutoEnrollStatusAsync(
            string organizationIdentifier,
            CancellationToken ct = default)
        {
            Operations.Add("autoEnroll");
            return Task.FromResult(new OrganizationAutoEnrollStatusResponse(
                OrganizationId,
                true));
        }

        public Task<OrganizationKeysResponse> GetOrganizationKeysAsync(
            string organizationId,
            CancellationToken ct = default)
        {
            Operations.Add("organizationKeys");
            return Task.FromResult(new OrganizationKeysResponse(
                Convert.ToBase64String(organizationPublicKey),
                null));
        }

        public Task<ConfigResponse> GetConfigAsync(CancellationToken ct = default)
        {
            Operations.Add("config");
            using var document = JsonDocument.Parse(featureEnabled ? "true" : "false");
            return Task.FromResult(new ConfigResponse(null, null, null)
            {
                FeatureStates = new Dictionary<string, JsonElement>
                {
                    [VaultCapabilityResolver.TdeV2JitProvisioningFeatureKey] =
                        document.RootElement.Clone(),
                },
            });
        }

        public Task<PostAccountKeysResponse> PostAccountKeysAsync(
            PostAccountKeysRequest request,
            CancellationToken ct = default)
        {
            Operations.Add("accountKeys");
            PostedKeys = request;
            return AccountKeysFailure is null
                ? Task.FromResult(new PostAccountKeysResponse(
                    null,
                    request.PublicKey,
                    request.EncryptedPrivateKey,
                    null))
                : Task.FromException<PostAccountKeysResponse>(AccountKeysFailure);
        }

        public Task EnrollResetPasswordAsync(
            string organizationId,
            string userId,
            ResetPasswordEnrollmentRequest request,
            CancellationToken ct = default)
        {
            Operations.Add("resetEnrollment");
            ResetEnrollment = request;
            return Task.CompletedTask;
        }

        public Task<DeviceDto> UpdateTrustedDeviceKeysAsync(
            string identifier,
            TrustedDeviceKeysRequest request,
            CancellationToken ct = default)
        {
            Operations.Add("deviceKeys");
            DeviceKeys = request;
            return Task.FromResult(new DeviceDto(
                "server-device-id",
                "Desktop",
                6,
                identifier,
                DateTimeOffset.UtcNow,
                true));
        }

        public Task UntrustDevicesAsync(
            UntrustDevicesRequest request,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<SyncResponse> GetSyncAsync(CancellationToken ct = default)
        {
            Operations.Add("sync");
            var request = PostedKeys!.AccountKeys;
            var response = new AccountKeysResponse(
                new PublicKeyEncryptionKeyPairResponse(
                    request.PublicKeyEncryptionKeyPair.WrappedPrivateKey,
                    request.PublicKeyEncryptionKeyPair.PublicKey,
                    request.PublicKeyEncryptionKeyPair.SignedPublicKey),
                new SignatureKeyPairResponse(
                    request.SignatureKeyPair.WrappedSigningKey,
                    request.SignatureKeyPair.VerifyingKey),
                request.SecurityState);
            var json = JsonSerializer.Serialize(
                response,
                ApiJsonContext.Default.AccountKeysResponse);
            using var document = JsonDocument.Parse(json);
            return Task.FromResult(new SyncResponse(
                "sync",
                new ProfileDto(accountId, "me@example.com", "Me", null, null)
                {
                    AccountKeys = document.RootElement.Clone(),
                    SecurityStamp = "stamp-1",
                },
                [],
                []));
        }

        public Task<ListResponse<DeviceDto>> GetDevicesAsync(CancellationToken ct = default)
        {
            Operations.Add("devices");
            return Task.FromResult(new ListResponse<DeviceDto>(
                [new DeviceDto("server-device-id", "Desktop", 6, deviceIdentifier, DateTimeOffset.UtcNow, true)],
                null,
                "list"));
        }

        public Task<FolderDto> GetFolderAsync(string folderId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SendResponseDto> GetSendAsync(string sendId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDeviceKeyStore : IDeviceKeyStore
    {
        private readonly Dictionary<DeviceKeyBinding, byte[]> _keys = [];

        public bool TryLoad(DeviceKeyBinding binding, out byte[] deviceKey)
        {
            if (_keys.TryGetValue(binding, out var stored))
            {
                deviceKey = stored.ToArray();
                return true;
            }
            deviceKey = [];
            return false;
        }

        public void Save(DeviceKeyBinding binding, ReadOnlySpan<byte> deviceKey) =>
            _keys[binding] = deviceKey.ToArray();

        public bool Remove(DeviceKeyBinding binding)
        {
            if (!_keys.Remove(binding, out var removed))
                return false;
            CryptographicOperations.ZeroMemory(removed);
            return true;
        }

        public bool Contains(DeviceKeyBinding binding) => _keys.ContainsKey(binding);
    }

    private sealed class FakeTdeV2JitPendingStore(List<string> operations)
        : ITdeV2JitPendingStore
    {
        private readonly Guid _entryId = Guid.NewGuid();
        private TdeV2JitPending? _pending;
        private long _revision;

        public TdeV2JitPendingStage? Stage { get; private set; }

        public TdeV2JitPendingReadResult Read(DeviceKeyBinding binding)
        {
            if (_pending is null || _pending.Binding != binding || Stage is null)
                return TdeV2JitPendingReadResult.Absent;
            return TdeV2JitPendingReadResult.Found(CreateRecord());
        }

        public TdeV2JitPendingStoreRecord? TryCreate(TdeV2JitPending pending)
        {
            if (_pending is not null)
                return null;
            _pending = pending.Clone();
            _revision = 1;
            Stage = TdeV2JitPendingStage.Prepared;
            operations.Add("ledger:create");
            return CreateRecord();
        }

        public TdeV2JitPendingStoreRecord? TryAdvance(
            TdeV2JitPendingStoreRecord expected,
            TdeV2JitPendingStage nextStage)
        {
            if (!Matches(expected) || Stage is null || nextStage <= Stage)
                return null;
            _revision++;
            Stage = nextStage;
            operations.Add($"ledger:{nextStage}");
            expected.Dispose();
            return CreateRecord();
        }

        public bool Remove(
            TdeV2JitPendingStoreRecord expected,
            TdeV2JitPendingRemovalReason reason)
        {
            if (!Matches(expected)
                || reason == TdeV2JitPendingRemovalReason.Completed
                && Stage != TdeV2JitPendingStage.LocalCommitReady)
            {
                return false;
            }
            operations.Add("ledger:remove");
            _pending!.Dispose();
            _pending = null;
            Stage = null;
            expected.Dispose();
            return true;
        }

        public void AdvanceForTest(TdeV2JitPendingStage nextStage)
        {
            var next = TryAdvance(CreateRecord(), nextStage)
                ?? throw new InvalidOperationException("The test stage could not be advanced.");
            next.Dispose();
        }

        private bool Matches(TdeV2JitPendingStoreRecord expected) =>
            _pending is not null
            && Stage is not null
            && expected.EntryId == _entryId
            && expected.Revision == _revision
            && expected.Stage == Stage;

        private TdeV2JitPendingStoreRecord CreateRecord() => new(
            _entryId,
            _revision,
            Stage!.Value,
            _pending!.Clone());
    }

    private sealed class FakeDeviceTrustService : IDeviceTrustService
    {
        public bool CapabilitySupported { get; private set; }
        public bool CanTrustCurrentDevice => false;
        public bool IsCurrentDeviceTrusted => false;

        public void SetCurrentSessionCapability(
            Core.Session.VaultSessionLease lease,
            bool supported) => CapabilitySupported = supported;

        public bool TryDecryptUserKey(
            string accountId,
            ServerEndpoints endpoints,
            string deviceIdentifier,
            string encryptedPrivateKey,
            string encryptedUserKey,
            out byte[] userKey)
        {
            userKey = [];
            return false;
        }

        public Task TrustCurrentDeviceAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FixedDeviceIdentifierProvider(string identifier)
        : IDeviceIdentifierProvider
    {
        public string GetDeviceIdentifier() => identifier;
    }
}
