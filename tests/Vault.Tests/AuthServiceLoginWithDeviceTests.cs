using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Crypto.V2;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class AuthServiceLoginWithDeviceTests
{
    private const string Email = "me@example.com";
    private const string AccountId = "11111111-1111-1111-1111-111111111111";
    private const string DeviceId = "22222222-2222-2222-2222-222222222222";
    private const string RequestId = "33333333-3333-3333-3333-333333333333";
    private const string AccessCode = "Aa12345678901234567890123";
    private const string MasterPassword = "correct horse battery staple";
    private const string SsoVerifier =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_";
    private static readonly DateTimeOffset Start =
        new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BeginAndPollPending_ExposeOnlyPublicRequestState()
    {
        var clock = new ControllableTimeProvider(Start);
        var loginWithDevice = new ControlledLoginWithDeviceService(clock);
        loginWithDevice.EnqueuePoll(new LoginWithDevicePollResult(
            LoginWithDevicePollStatus.Pending));
        var fixture = CreateFixture(loginWithDevice, new RecordingIdentityApiClient(), clock);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        var begun = Assert.IsType<AuthResult.LoginWithDeviceWaiting>(
            await fixture.Auth.BeginLoginWithDeviceAsync(
                attempt,
                "https://vault.example",
                Email,
                TestContext.Current.CancellationToken));
        var polled = Assert.IsType<AuthResult.LoginWithDevicePending>(
            await fixture.Auth.PollLoginWithDeviceAsync(
                attempt,
                TestContext.Current.CancellationToken));

        Assert.Equal(RequestId, begun.RequestId);
        Assert.Equal("one-two-three", begun.FingerprintPhrase);
        Assert.Equal(LoginWithDeviceUiStatus.Waiting, begun.Status);
        Assert.Equal(begun.RequestId, polled.RequestId);
        Assert.Equal(begun.FingerprintPhrase, polled.FingerprintPhrase);
        Assert.Equal(LoginWithDeviceUiStatus.Pending, polled.Status);
        Assert.Equal(1, loginWithDevice.BeginCount);
        Assert.Equal(1, loginWithDevice.PollCount);
    }

    [Fact]
    public async Task ApprovedV1_UsesAuthRequestGrantOnceAndPersistsSession()
    {
        var userKey = Enumerable.Range(32, 64).Select(value => (byte)value).ToArray();
        var grantBuffer = userKey.ToArray();
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(grantBuffer, securityVersion: 1));
        var identity = new RecordingIdentityApiClient();
        var protectedUserKey = CreateProtectedUserKey(userKey);
        identity.Enqueue(new ConnectTokenResult.Success(V1Token(protectedUserKey) with
        {
            UserDecryptionOptions = new UserDecryptionOptionsResponse(true),
        }));
        var fixture = CreateFixture(loginWithDevice, identity);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal(userKey, fixture.Session.UserKey!.FullKey);
        Assert.All(grantBuffer, value => Assert.Equal(0, value));
        var request = Assert.Single(identity.TokenRequests);
        Assert.Equal("password", request.GrantType);
        Assert.Equal(Email, request.Username);
        Assert.Equal(RequestId, request.AuthRequestId);
        Assert.Equal(DeviceId, request.DeviceIdentifier);
        Assert.Equal(AccessCode, request.PasswordHash);
        Assert.Null(request.NewDeviceOtp);
        Assert.True(fixture.TokenStore.TryLoad(out var persisted));
        Assert.Equal(protectedUserKey, persisted.ProtectedUserKey);
        Assert.Equal(DeviceId, persisted.DeviceIdentifier);
        Assert.Equal(KdfType.Pbkdf2, persisted.KdfType);
        Assert.Equal(600_000, persisted.KdfIterations);
        Assert.True(persisted.HasMasterPassword);
        Assert.Equal(1, fixture.QuarantineStore.ArmCount);
        Assert.Null(fixture.QuarantineStore.Current);
        Assert.Contains(
            LoginWithDeviceQuarantineRemovalReason.SessionPersisted,
            fixture.QuarantineStore.RemovalReasons);
    }

    [Fact]
    public async Task ApprovedV1_NestedOnlyMasterPasswordWrapperPersistsCanonicalUnlockData()
    {
        var userKey = Enumerable.Range(32, 64).Select(value => (byte)value).ToArray();
        var grantBuffer = userKey.ToArray();
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(grantBuffer, securityVersion: 1));
        var protectedUserKey = CreateProtectedUserKey(userKey);
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.Success(V1Token(protectedUserKey) with
        {
            Key = null,
            UserDecryptionOptions = new UserDecryptionOptionsResponse(
                true,
                new MasterPasswordUnlockResponse(
                    new PreloginKdfSettingsResponse(
                        KdfType.Pbkdf2,
                        600_000,
                        null,
                        null),
                    null,
                    protectedUserKey,
                    Email)),
        }));
        var fixture = CreateFixture(loginWithDevice, identity);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.True(fixture.TokenStore.TryLoad(out var persisted));
        Assert.Equal(protectedUserKey, persisted.ProtectedUserKey);
        Assert.Equal(PersistedSessionUnlockMethod.MasterPassword, persisted.UnlockMethod);
        Assert.Equal(1, persisted.UserKeySecurityVersion);
        Assert.All(grantBuffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ApprovedDeviceLogin_PendingAccountDeletionDoesNotEstablishSession()
    {
        var userKey = Enumerable.Range(32, 64).Select(value => (byte)value).ToArray();
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(userKey.ToArray(), securityVersion: 1));
        var identity = new RecordingIdentityApiClient();
        var protectedUserKey = CreateProtectedUserKey(userKey);
        identity.Enqueue(new ConnectTokenResult.Success(V1Token(protectedUserKey)));
        var bindingSession = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            AccountId,
            ServerEndpoints.FromWebVault("https://vault.example"),
            1,
            Email,
            DeviceId,
            "unused-refresh",
            protectedUserKey,
            KdfType.Pbkdf2,
            600_000,
            null,
            null,
            Email);
        var fixture = CreateFixture(
            loginWithDevice,
            identity,
            accountDeletionTombstones:
                FixedAccountDeletionTombstoneStore.ForSession(bindingSession));
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AuthFailureKind.AccountDeletionRecoveryRequired,
            Assert.IsType<AuthResult.Failure>(result).Kind);
        Assert.Equal(VaultState.LoggedOut, fixture.Session.State);
        Assert.False(fixture.TokenStore.TryLoad(out _));
    }

    [Fact]
    public async Task ApprovedV1_VaultwardenAccountKeysShapeStillUnlocksLegacySession()
    {
        var userKey = Enumerable.Range(32, 64).Select(value => (byte)value).ToArray();
        var grantBuffer = userKey.ToArray();
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(grantBuffer, securityVersion: 1));
        var identity = new RecordingIdentityApiClient();
        var protectedUserKey = CreateProtectedUserKey(userKey);
        var token = V1Token(protectedUserKey) with
        {
            AccountKeys = new AccountKeysResponse(
                new PublicKeyEncryptionKeyPairResponse(
                    protectedUserKey,
                    "vaultwarden-public-key")),
        };
        identity.Enqueue(new ConnectTokenResult.Success(token));
        var fixture = CreateFixture(loginWithDevice, identity);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.False(fixture.Session.UsesV2UserKey);
        Assert.Equal(userKey, fixture.Session.UserKey!.FullKey);
        Assert.All(grantBuffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ApprovedV1_MatchingPersistedBindingSendsRememberPayloadAndSavesRotatedToken()
    {
        var userKey = Enumerable.Range(32, 64).Select(value => (byte)value).ToArray();
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(userKey.ToArray(), securityVersion: 1));
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.Success(
            V1Token(CreateProtectedUserKey(userKey), "rotated-remember")));
        var fixture = CreateFixture(loginWithDevice, identity);
        fixture.TokenStore.Save(SavedSession(
            email: "  ME@EXAMPLE.COM  ",
            deviceIdentifier: DeviceId.ToUpperInvariant(),
            twoFactorToken: "stored-remember"));
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        var request = Assert.Single(identity.TokenRequests);
        Assert.Equal((int)TwoFactorProviderType.Remember, request.TwoFactor!.Provider);
        Assert.Equal("stored-remember", request.TwoFactor.Token);
        Assert.False(request.TwoFactor.Remember);
        Assert.True(fixture.TokenStore.TryLoad(out var persisted));
        Assert.Equal("rotated-remember", persisted.TwoFactorToken);
    }

    [Theory]
    [InlineData(RememberBindingMismatch.Email)]
    [InlineData(RememberBindingMismatch.Device)]
    [InlineData(RememberBindingMismatch.Origin)]
    [InlineData(RememberBindingMismatch.EmptyToken)]
    public async Task InitialExchange_MismatchedBindingOrEmptyTokenDoesNotSendRememberPayload(
        RememberBindingMismatch mismatch)
    {
        var userKey = Enumerable.Range(32, 64).Select(value => (byte)value).ToArray();
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(userKey, securityVersion: 1));
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.TwoFactorRequired(
            [(int)TwoFactorProviderType.Authenticator]));
        var fixture = CreateFixture(loginWithDevice, identity);
        var persisted = mismatch switch
        {
            RememberBindingMismatch.Email => SavedSession(
                email: "other@example.com",
                twoFactorToken: "stored-remember"),
            RememberBindingMismatch.Device => SavedSession(
                deviceIdentifier: "44444444-4444-4444-4444-444444444444",
                twoFactorToken: "stored-remember"),
            RememberBindingMismatch.Origin => SavedSession(
                endpoints: ServerEndpoints.FromWebVault("https://other.example"),
                twoFactorToken: "stored-remember"),
            RememberBindingMismatch.EmptyToken => SavedSession(twoFactorToken: string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };
        fixture.TokenStore.Save(persisted);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.TwoFactorRequired>(result);
        Assert.Null(Assert.Single(identity.TokenRequests).TwoFactor);
    }

    [Fact]
    public async Task PersistedReadFailure_DegradesToNoRememberAndStillAuthenticates()
    {
        var userKey = Enumerable.Range(32, 64).Select(value => (byte)value).ToArray();
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(userKey.ToArray(), securityVersion: 1));
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.Success(
            V1Token(CreateProtectedUserKey(userKey))));
        var tokenStore = new ThrowingReadTokenStore();
        var fixture = CreateFixture(
            loginWithDevice,
            identity,
            tokenStore: tokenStore);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.Null(Assert.Single(identity.TokenRequests).TwoFactor);
        Assert.NotNull(tokenStore.LastSaved);
        Assert.Null(tokenStore.LastSaved.TwoFactorToken);
    }

    [Fact]
    public async Task ApprovedV2_AuthenticatesAccountKeysAndUnlocksV2Session()
    {
        using var material = V2AccountKeyMaterial.Generate();
        var transport = material.UserKey.ExportTransport();
        var grantBuffer = transport.ToArray();
        CryptographicOperations.ZeroMemory(transport);
        var accountKeys = ToAccountKeysResponse(material);
        var protectedUserKey = CreatePasswordProtectedV2UserKey(material.UserKey);
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(grantBuffer, securityVersion: 2));
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.Success(new TokenResponse(
            AccessToken(),
            "refresh",
            3600,
            "Bearer",
            "api offline_access",
            protectedUserKey,
            null,
            KdfType.Pbkdf2,
            600_000,
            AccountKeys: accountKeys)));
        var fixture = CreateFixture(
            loginWithDevice,
            identity,
            sync: Sync(accountKeys));
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.True(fixture.Session.UsesV2UserKey);
        Assert.Equal(material.WrappedPrivateKey, fixture.Session.EncryptedPrivateKey);
        Assert.All(grantBuffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ApprovedV2_NoMasterPasswordPersistsTrustedDeviceUnlockState()
    {
        using var material = V2AccountKeyMaterial.Generate();
        var transport = material.UserKey.ExportTransport();
        var grantBuffer = transport.ToArray();
        CryptographicOperations.ZeroMemory(transport);
        var accountKeys = ToAccountKeysResponse(material);
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(grantBuffer, securityVersion: 2));
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.Success(new TokenResponse(
            AccessToken(),
            "refresh",
            3600,
            "Bearer",
            "api offline_access",
            null,
            null,
            KdfType.Pbkdf2,
            600_000,
            UserDecryptionOptions: new UserDecryptionOptionsResponse(
                false,
                TrustedDeviceOption: new TrustedDeviceUserDecryptionOptionResponse(
                    false,
                    false,
                    false,
                    false)),
            AccountKeys: accountKeys)));
        var tokenStore = new MemoryTokenStore();
        var fixture = CreateFixture(
            loginWithDevice,
            identity,
            sync: Sync(accountKeys),
            tokenStore: tokenStore);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.True(fixture.Session.UsesV2UserKey);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal(PersistedSessionUnlockMethod.TrustedDeviceSso, persisted.UnlockMethod);
        Assert.Equal(string.Empty, persisted.ProtectedUserKey);
        Assert.Equal(2, persisted.UserKeySecurityVersion);
        Assert.Equal(material.UserKey.KeyIdHex, persisted.UserKeyId);
        Assert.All(grantBuffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ApprovedV2_NoMasterPasswordRejectsOffboardingTrustedDeviceOption()
    {
        using var material = V2AccountKeyMaterial.Generate();
        var transport = material.UserKey.ExportTransport();
        var grantBuffer = transport.ToArray();
        CryptographicOperations.ZeroMemory(transport);
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(grantBuffer, securityVersion: 2));
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.Success(new TokenResponse(
            AccessToken(),
            "refresh",
            3600,
            "Bearer",
            "api offline_access",
            null,
            null,
            KdfType.Pbkdf2,
            600_000,
            UserDecryptionOptions: new UserDecryptionOptionsResponse(
                false,
                TrustedDeviceOption: new TrustedDeviceUserDecryptionOptionResponse(
                    false,
                    false,
                    false,
                    true)),
            AccountKeys: ToAccountKeysResponse(material))));
        var tokenStore = new MemoryTokenStore();
        var fixture = CreateFixture(
            loginWithDevice,
            identity,
            tokenStore: tokenStore);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.LoginWithDeviceOutcomeUncertain>(result);
        Assert.False(tokenStore.TryLoad(out _));
        Assert.Equal(VaultState.LoggedOut, fixture.Session.State);
        Assert.All(grantBuffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ApprovedV2_MissingRequiredAccountKeyFailsClosed()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;
        using var material = V2AccountKeyMaterial.Generate();
        var transport = material.UserKey.ExportTransport();
        var grantBuffer = transport.ToArray();
        CryptographicOperations.ZeroMemory(transport);
        var completeAccountKeys = ToAccountKeysResponse(material);
        var incompleteAccountKeys = completeAccountKeys with
        {
            SignatureKeyPair = completeAccountKeys.SignatureKeyPair! with
            {
                WrappedSigningKey = null,
            },
        };
        var protectedUserKey = CreatePasswordProtectedV2UserKey(material.UserKey);
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(grantBuffer, securityVersion: 2));
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.Success(new TokenResponse(
            AccessToken(),
            "refresh",
            3600,
            "Bearer",
            "api offline_access",
            protectedUserKey,
            null,
            KdfType.Pbkdf2,
            600_000,
            AccountKeys: incompleteAccountKeys)));
        var fixture = CreateFixture(loginWithDevice, identity);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.LoginWithDeviceOutcomeUncertain>(result);
        Assert.Equal(VaultState.LoggedOut, fixture.Session.State);
        Assert.All(grantBuffer, value => Assert.Equal(0, value));
        Assert.False(fixture.TokenStore.TryLoad(out _));
        Assert.NotNull(fixture.QuarantineStore.Current);
    }

    [Fact]
    public async Task TwoFactorAndEmailResend_PreserveBoundGrantUntilSuccess()
    {
        var userKey = Enumerable.Range(64, 64).Select(value => (byte)value).ToArray();
        var grantBuffer = userKey.ToArray();
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(grantBuffer, securityVersion: 1));
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.TwoFactorRequired(
            [(int)TwoFactorProviderType.Email]));
        identity.Enqueue(new ConnectTokenResult.Success(
            V1Token(CreateProtectedUserKey(userKey), "remember-token")));
        var fixture = CreateFixture(loginWithDevice, identity);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var challenged = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);
        var resent = await fixture.Auth.ResendTwoFactorEmailAsync(
            attempt,
            TestContext.Current.CancellationToken);
        var result = await fixture.Auth.SubmitTwoFactorAsync(
            attempt,
            "123456",
            (int)TwoFactorProviderType.Email,
            TestContext.Current.CancellationToken,
            rememberDevice: true);

        Assert.IsType<AuthResult.TwoFactorRequired>(challenged);
        Assert.True(resent);
        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal(2, identity.EmailRequests.Count);
        Assert.All(identity.EmailRequests, request =>
        {
            Assert.Equal(RequestId, request.AuthRequestId);
            Assert.Equal(AccessCode, request.AuthRequestAccessCode);
            Assert.Equal(DeviceId, request.DeviceIdentifier);
        });
        Assert.Equal(2, identity.TokenRequests.Count);
        Assert.Null(identity.TokenRequests[0].TwoFactor);
        Assert.All(identity.TokenRequests, request =>
        {
            Assert.Equal(RequestId, request.AuthRequestId);
            Assert.Equal(AccessCode, request.PasswordHash);
        });
        var submittedTwoFactor = identity.TokenRequests[1].TwoFactor!;
        Assert.Equal((int)TwoFactorProviderType.Email, submittedTwoFactor.Provider);
        Assert.Equal("123456", submittedTwoFactor.Token);
        Assert.True(submittedTwoFactor.Remember);
        Assert.All(grantBuffer, value => Assert.Equal(0, value));
        Assert.True(fixture.TokenStore.TryLoad(out var persisted));
        Assert.Equal("remember-token", persisted.TwoFactorToken);
        Assert.Equal(1, fixture.QuarantineStore.ArmCount);
        Assert.Null(fixture.QuarantineStore.Current);
    }

    [Fact]
    public async Task ForcePasswordReset_RemovesPriorArtifactsAndRequiresPasswordLogin()
    {
        var userKey = new byte[64];
        RandomNumberGenerator.Fill(userKey);
        var grantBuffer = userKey.ToArray();
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(grantBuffer, securityVersion: 1));
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.Success(new TokenResponse(
            AccessToken(),
            "refresh",
            3600,
            "Bearer",
            "api offline_access",
            null,
            null,
            KdfType.Pbkdf2,
            0,
            ForcePasswordReset: true)));
        var cacheStore = new FakeCacheStore();
        var fixture = CreateFixture(
            loginWithDevice,
            identity,
            cacheStore: cacheStore);
        fixture.TokenStore.Save(SavedSession());
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.SafeMessage, failure.Kind);
        Assert.Contains("temporary password", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.TokenStore.GetSavedAccounts());
        Assert.Equal(1, cacheStore.ClearCount);
        Assert.All(grantBuffer, value => Assert.Equal(0, value));
        Assert.Equal(VaultState.LoggedOut, fixture.Session.State);
        Assert.NotNull(fixture.QuarantineStore.Current);
    }

    [Fact]
    public async Task TokenExchangeNetworkFailure_IsOutcomeUncertainAndCannotReplayGrant()
    {
        var grantBuffer = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(grantBuffer, securityVersion: 1));
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(_ => Task.FromException<ConnectTokenResult>(
            new HttpRequestException("response lost")));
        var fixture = CreateFixture(loginWithDevice, identity);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);
        var replay = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.LoginWithDeviceOutcomeUncertain>(result);
        Assert.Equal(
            AuthFailureKind.SessionExpired,
            Assert.IsType<AuthResult.Failure>(replay).Kind);
        Assert.All(grantBuffer, value => Assert.Equal(0, value));
        Assert.Single(identity.TokenRequests);
        Assert.NotNull(fixture.QuarantineStore.Current);
    }

    [Fact]
    public async Task CancelDuringTokenExchange_ReportsOutcomeUncertainAndZerosGrant()
    {
        var grantBuffer = Enumerable.Range(1, 64).Select(value => (byte)value).ToArray();
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(grantBuffer, securityVersion: 1));
        var exchangeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(async ct =>
        {
            exchangeStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            throw new InvalidOperationException("The blocked exchange completed unexpectedly.");
        });
        var fixture = CreateFixture(loginWithDevice, identity);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);

        var poll = fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);
        await exchangeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var cancellation = fixture.Auth.CancelLoginWithDevice(attempt);
        var result = await poll;

        Assert.Equal(
            LoginWithDeviceCancellationStatus.OutcomeUncertain,
            cancellation.Status);
        Assert.IsType<AuthResult.LoginWithDeviceOutcomeUncertain>(result);
        Assert.All(grantBuffer, value => Assert.Equal(0, value));
        Assert.Equal(VaultState.LoggedOut, fixture.Session.State);
        Assert.NotNull(fixture.QuarantineStore.Current);
    }

    [Fact]
    public async Task ConcurrentPolls_CallRequestServiceOnlyOnce()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(async _ =>
        {
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return new LoginWithDevicePollResult(LoginWithDevicePollStatus.Pending);
        });
        var fixture = CreateFixture(loginWithDevice, new RecordingIdentityApiClient());
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);

        var first = fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);
        release.TrySetResult();
        var firstResult = await first;

        Assert.Equal(
            LoginWithDeviceUiStatus.Pending,
            Assert.IsType<AuthResult.LoginWithDevicePending>(second).Status);
        Assert.IsType<AuthResult.LoginWithDevicePending>(firstResult);
        Assert.Equal(1, loginWithDevice.PollCount);
    }

    [Fact]
    public async Task TwoFactorContext_ExpiresActivelyAndZerosGrantWithoutInteraction()
    {
        var clock = new ControllableTimeProvider(Start);
        var grantBuffer = Enumerable.Range(1, 64).Select(value => (byte)value).ToArray();
        var loginWithDevice = new ControlledLoginWithDeviceService(clock);
        loginWithDevice.EnqueuePoll(ApprovedGrant(grantBuffer, securityVersion: 1));
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.TwoFactorRequired(
            [(int)TwoFactorProviderType.Authenticator]));
        var fixture = CreateFixture(loginWithDevice, identity, clock);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.TwoFactorRequired>(
            await fixture.Auth.PollLoginWithDeviceAsync(
                attempt,
                TestContext.Current.CancellationToken));
        Assert.Contains(grantBuffer, value => value != 0);
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.All(grantBuffer, value => Assert.Equal(0, value));
        Assert.False(fixture.Session.IsCurrent(attempt));
        Assert.Null(fixture.QuarantineStore.Current);
        Assert.Contains(
            LoginWithDeviceQuarantineRemovalReason.DefiniteOutcome,
            fixture.QuarantineStore.RemovalReasons);
        var submit = await fixture.Auth.SubmitTwoFactorAsync(
            attempt,
            "123456",
            (int)TwoFactorProviderType.Authenticator,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            AuthFailureKind.SessionExpired,
            Assert.IsType<AuthResult.Failure>(submit).Kind);
        Assert.Single(identity.TokenRequests);
    }

    [Fact]
    public async Task TwoFactorExpiryDuringTokenExchange_ReturnsOutcomeUncertainAndZerosGrant()
    {
        var clock = new ControllableTimeProvider(Start);
        var grantBuffer = Enumerable.Range(1, 64).Select(value => (byte)value).ToArray();
        var loginWithDevice = new ControlledLoginWithDeviceService(clock);
        loginWithDevice.EnqueuePoll(ApprovedGrant(grantBuffer, securityVersion: 1));
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.TwoFactorRequired(
            [(int)TwoFactorProviderType.Authenticator]));
        var exchangeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        identity.Enqueue(async ct =>
        {
            exchangeStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            throw new InvalidOperationException("The blocked exchange completed unexpectedly.");
        });
        var fixture = CreateFixture(loginWithDevice, identity, clock);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        var submit = fixture.Auth.SubmitTwoFactorAsync(
            attempt,
            "123456",
            (int)TwoFactorProviderType.Authenticator,
            TestContext.Current.CancellationToken);
        await exchangeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(5));
        var result = await submit;

        Assert.IsType<AuthResult.LoginWithDeviceOutcomeUncertain>(result);
        Assert.All(grantBuffer, value => Assert.Equal(0, value));
        Assert.NotNull(fixture.QuarantineStore.Current);
    }

    [Fact]
    public async Task TwoFactorExpiryDuringSessionMutation_RevokesPersistedPromotion()
    {
        var clock = new ControllableTimeProvider(Start);
        var tokenStore = new CallbackTokenStore(
            onSave: () => clock.Advance(TimeSpan.FromMinutes(5)));
        var cacheStore = new FakeCacheStore();
        var userKey = Enumerable.Range(1, 64).Select(value => (byte)value).ToArray();
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.TwoFactorRequired(
            [(int)TwoFactorProviderType.Authenticator]));
        identity.Enqueue(new ConnectTokenResult.Success(
            V1Token(CreateProtectedUserKey(userKey))));
        var loginWithDevice = new ControlledLoginWithDeviceService(clock);
        loginWithDevice.EnqueuePoll(ApprovedGrant(userKey.ToArray(), securityVersion: 1));
        var syncCalls = 0;
        var fixture = CreateFixture(
            loginWithDevice,
            identity,
            timeProvider: clock,
            cacheStore: cacheStore,
            tokenStore: tokenStore,
            syncHandler: _ =>
            {
                syncCalls++;
                return Task.FromResult(Sync());
            });
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        Assert.IsType<AuthResult.TwoFactorRequired>(
            await fixture.Auth.PollLoginWithDeviceAsync(
                attempt,
                TestContext.Current.CancellationToken));

        var result = await fixture.Auth.SubmitTwoFactorAsync(
            attempt,
            "123456",
            (int)TwoFactorProviderType.Authenticator,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.LoginWithDeviceOutcomeUncertain>(result);
        Assert.Equal(VaultState.Locked, fixture.Session.State);
        Assert.False(tokenStore.HasSession);
        Assert.True(tokenStore.RemoveCount > 0);
        Assert.True(cacheStore.ClearCount > 0);
        Assert.Equal(0, syncCalls);
    }

    [Fact]
    public async Task TwoFactorExpiryImmediatelyAfterCommit_RevokesBeforeBootstrap()
    {
        var clock = new ControllableTimeProvider(Start);
        var tokenStore = new CallbackTokenStore(
            onSave: () => clock.DeferAdvanceUntilGet(
                callsBeforeAdvance: 2,
                duration: TimeSpan.FromMinutes(5)));
        var cacheStore = new FakeCacheStore();
        var userKey = Enumerable.Range(1, 64).Select(value => (byte)value).ToArray();
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.TwoFactorRequired(
            [(int)TwoFactorProviderType.Authenticator]));
        identity.Enqueue(new ConnectTokenResult.Success(
            V1Token(CreateProtectedUserKey(userKey))));
        var loginWithDevice = new ControlledLoginWithDeviceService(clock);
        loginWithDevice.EnqueuePoll(ApprovedGrant(userKey.ToArray(), securityVersion: 1));
        var syncCalls = 0;
        var fixture = CreateFixture(
            loginWithDevice,
            identity,
            timeProvider: clock,
            cacheStore: cacheStore,
            tokenStore: tokenStore,
            syncHandler: _ =>
            {
                syncCalls++;
                return Task.FromResult(Sync());
            });
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        Assert.IsType<AuthResult.TwoFactorRequired>(
            await fixture.Auth.PollLoginWithDeviceAsync(
                attempt,
                TestContext.Current.CancellationToken));

        var result = await fixture.Auth.SubmitTwoFactorAsync(
            attempt,
            "123456",
            (int)TwoFactorProviderType.Authenticator,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.LoginWithDeviceOutcomeUncertain>(result);
        Assert.Equal(VaultState.Locked, fixture.Session.State);
        Assert.False(tokenStore.HasSession);
        Assert.True(tokenStore.RemoveCount > 0);
        Assert.True(cacheStore.ClearCount > 0);
        Assert.Equal(0, syncCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelPromotedSession_WhenExactCleanupFailsReportsUncertainAndLocks(
        bool throws)
    {
        var tokenStore = new CallbackTokenStore(
            rejectRemove: !throws,
            removeException: throws ? new IOException("remove failed") : null);
        var cacheStore = new FakeCacheStore();
        var userKey = Enumerable.Range(1, 64).Select(value => (byte)value).ToArray();
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.Success(
            V1Token(CreateProtectedUserKey(userKey))));
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(userKey.ToArray(), securityVersion: 1));
        var bootstrapStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = CreateFixture(
            loginWithDevice,
            identity,
            cacheStore: cacheStore,
            tokenStore: tokenStore,
            syncHandler: async ct =>
            {
                bootstrapStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Sync();
            });
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);

        var polling = fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);
        await bootstrapStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var cancellation = fixture.Auth.CancelLoginWithDevice(attempt);
        var result = await polling;

        Assert.Equal(
            LoginWithDeviceCancellationStatus.OutcomeUncertain,
            cancellation.Status);
        Assert.IsType<AuthResult.LoginWithDeviceOutcomeUncertain>(result);
        Assert.Equal(VaultState.Locked, fixture.Session.State);
        Assert.True(tokenStore.HasSession);
        Assert.Equal(1, tokenStore.RemoveCount);
        Assert.True(cacheStore.ClearCount > 0);
    }

    [Theory]
    [InlineData(LoginWithDeviceQuarantineStoreReadStatus.Found)]
    [InlineData(LoginWithDeviceQuarantineStoreReadStatus.Corrupt)]
    [InlineData(LoginWithDeviceQuarantineStoreReadStatus.Unavailable)]
    public async Task AvailabilityAndBegin_OnlyAbsentQuarantineStateCanProceed(
        LoginWithDeviceQuarantineStoreReadStatus status)
    {
        var store = new RecordingLoginWithDeviceQuarantineStore();
        if (status == LoginWithDeviceQuarantineStoreReadStatus.Found)
            store.Seed(QuarantineRecord());
        else
            store.ForcedReadStatus = status;
        var publicAuthRequest = new RecordingPublicAuthRequestApiClient();
        var loginWithDevice = new ControlledLoginWithDeviceService();
        var fixture = CreateFixture(
            loginWithDevice,
            new RecordingIdentityApiClient(),
            quarantineStore: store,
            publicAuthRequest: publicAuthRequest);

        var available = await fixture.Auth.IsLoginWithDeviceAvailableAsync(
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var info = await fixture.Auth.GetLoginWithDeviceQuarantineInfoAsync(
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        var begun = await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);

        Assert.False(available);
        Assert.Equal(
            AuthFailureKind.SafeMessage,
            Assert.IsType<AuthResult.Failure>(begun).Kind);
        Assert.Equal(0, publicAuthRequest.KnownDeviceCount);
        Assert.Equal(0, loginWithDevice.BeginCount);
        Assert.Equal(3, store.ReadCount);
        Assert.Equal(
            status,
            info switch
            {
                LoginWithDeviceQuarantineInfo.Found =>
                    LoginWithDeviceQuarantineStoreReadStatus.Found,
                LoginWithDeviceQuarantineInfo.Corrupt =>
                    LoginWithDeviceQuarantineStoreReadStatus.Corrupt,
                LoginWithDeviceQuarantineInfo.Unavailable =>
                    LoginWithDeviceQuarantineStoreReadStatus.Unavailable,
                _ => LoginWithDeviceQuarantineStoreReadStatus.Absent,
            });
    }

    [Fact]
    public async Task AvailabilityAndBegin_WhenQuarantineAbsentBothReadAndProceed()
    {
        var loginWithDevice = new ControlledLoginWithDeviceService();
        var fixture = CreateFixture(
            loginWithDevice,
            new RecordingIdentityApiClient());

        Assert.True(await fixture.Auth.IsLoginWithDeviceAvailableAsync(
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken));
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        Assert.IsType<AuthResult.LoginWithDeviceWaiting>(
            await fixture.Auth.BeginLoginWithDeviceAsync(
                attempt,
                "https://vault.example",
                Email,
                TestContext.Current.CancellationToken));

        Assert.Equal(2, fixture.QuarantineStore.ReadCount);
        Assert.Equal(1, fixture.PublicAuthRequest.KnownDeviceCount);
        Assert.Equal(1, loginWithDevice.BeginCount);
    }

    [Fact]
    public async Task FirstTokenExchange_ArmsDurableRecordBeforeIdentityRequest()
    {
        var events = new List<string>();
        var store = new RecordingLoginWithDeviceQuarantineStore(events: events);
        var identity = new RecordingIdentityApiClient
        {
            OnConnectToken = () => events.Add("identity"),
        };
        var userKey = Enumerable.Range(1, 64).Select(value => (byte)value).ToArray();
        identity.Enqueue(new ConnectTokenResult.Success(
            V1Token(CreateProtectedUserKey(userKey))));
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(userKey.ToArray(), securityVersion: 1));
        var fixture = CreateFixture(
            loginWithDevice,
            identity,
            quarantineStore: store);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken));

        Assert.True(events.IndexOf("arm") >= 0);
        Assert.True(events.IndexOf("identity") > events.IndexOf("arm"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArmFailure_SendsNoIdentityRequest(bool throws)
    {
        var store = new RecordingLoginWithDeviceQuarantineStore
        {
            RejectArm = !throws,
            ArmException = throws ? new IOException("quarantine unavailable") : null,
        };
        var identity = new RecordingIdentityApiClient();
        var grantBuffer = Enumerable.Range(1, 64).Select(value => (byte)value).ToArray();
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(grantBuffer, securityVersion: 1));
        var fixture = CreateFixture(
            loginWithDevice,
            identity,
            quarantineStore: store);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);

        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.LoginWithDeviceOutcomeUncertain>(result);
        Assert.Empty(identity.TokenRequests);
        Assert.Equal(1, store.ArmCount);
        Assert.All(grantBuffer, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task UncertainIdentityStatus_PreservesDurableRecord(int status)
    {
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.Error("uncertain", status));
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(new byte[64], securityVersion: 1));
        var fixture = CreateFixture(loginWithDevice, identity);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);

        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.LoginWithDeviceOutcomeUncertain>(result);
        Assert.NotNull(fixture.QuarantineStore.Current);
        Assert.Empty(fixture.QuarantineStore.RemovalReasons);
    }

    [Fact]
    public async Task DefiniteIdentityRejection_RemovesExactDurableRecord()
    {
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.Error("rejected", 401));
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(new byte[64], securityVersion: 1));
        var fixture = CreateFixture(loginWithDevice, identity);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);

        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AuthFailureKind.InvalidCredentials,
            Assert.IsType<AuthResult.Failure>(result).Kind);
        Assert.Null(fixture.QuarantineStore.Current);
        Assert.Contains(
            LoginWithDeviceQuarantineRemovalReason.DefiniteOutcome,
            fixture.QuarantineStore.RemovalReasons);
    }

    [Fact]
    public async Task AbandonIdleDeviceTwoFactor_RemovesExactDurableRecord()
    {
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.TwoFactorRequired(
            [(int)TwoFactorProviderType.Authenticator]));
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(new byte[64], securityVersion: 1));
        var fixture = CreateFixture(loginWithDevice, identity);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        Assert.IsType<AuthResult.TwoFactorRequired>(
            await fixture.Auth.PollLoginWithDeviceAsync(
                attempt,
                TestContext.Current.CancellationToken));
        Assert.NotNull(fixture.QuarantineStore.Current);

        fixture.Auth.AbandonAuthenticationAttempt(attempt);

        Assert.Null(fixture.QuarantineStore.Current);
        Assert.Contains(
            LoginWithDeviceQuarantineRemovalReason.DefiniteOutcome,
            fixture.QuarantineStore.RemovalReasons);
    }

    [Fact]
    public async Task BeginBlockedByExistingMarker_DoesNotClearIdleTwoFactorRecord()
    {
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.TwoFactorRequired(
            [(int)TwoFactorProviderType.Authenticator]));
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(new byte[64], securityVersion: 1));
        var fixture = CreateFixture(loginWithDevice, identity);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);
        Assert.IsType<AuthResult.TwoFactorRequired>(
            await fixture.Auth.PollLoginWithDeviceAsync(
                attempt,
                TestContext.Current.CancellationToken));
        var marker = fixture.QuarantineStore.Current;

        var result = await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AuthFailureKind.SafeMessage,
            Assert.IsType<AuthResult.Failure>(result).Kind);
        Assert.Same(marker, fixture.QuarantineStore.Current);
    }

    [Fact]
    public async Task SessionSaveFailure_ReturnsUncertainAndPreservesDurableRecord()
    {
        var userKey = Enumerable.Range(1, 64).Select(value => (byte)value).ToArray();
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.Success(
            V1Token(CreateProtectedUserKey(userKey))));
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(userKey.ToArray(), securityVersion: 1));
        var fixture = CreateFixture(
            loginWithDevice,
            identity,
            tokenStore: new ThrowingWriteTokenStore());
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);

        var result = await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.LoginWithDeviceOutcomeUncertain>(result);
        Assert.NotNull(fixture.QuarantineStore.Current);
    }

    [Fact]
    public async Task SessionPersisted_RemoveFailureStillReturnsSuccessAndBlocksFutureBegin()
    {
        var store = new RecordingLoginWithDeviceQuarantineStore
        {
            RejectRemove = true,
        };
        var userKey = Enumerable.Range(1, 64).Select(value => (byte)value).ToArray();
        var identity = new RecordingIdentityApiClient();
        identity.Enqueue(new ConnectTokenResult.Success(
            V1Token(CreateProtectedUserKey(userKey))));
        var loginWithDevice = new ControlledLoginWithDeviceService();
        loginWithDevice.EnqueuePoll(ApprovedGrant(userKey.ToArray(), securityVersion: 1));
        var fixture = CreateFixture(
            loginWithDevice,
            identity,
            quarantineStore: store);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();
        await fixture.Auth.BeginLoginWithDeviceAsync(
            attempt,
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(await fixture.Auth.PollLoginWithDeviceAsync(
            attempt,
            TestContext.Current.CancellationToken));
        Assert.NotNull(store.Current);
        Assert.False(await fixture.Auth.IsLoginWithDeviceAvailableAsync(
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManualClear_RequiresEligibilityAndExactSnapshot()
    {
        var clock = new ControllableTimeProvider(Start);
        var store = new RecordingLoginWithDeviceQuarantineStore(clock);
        store.Seed(QuarantineRecord());
        var fixture = CreateFixture(
            new ControlledLoginWithDeviceService(clock),
            new RecordingIdentityApiClient(),
            timeProvider: clock,
            quarantineStore: store);
        var found = Assert.IsType<LoginWithDeviceQuarantineInfo.Found>(
            await fixture.Auth.GetLoginWithDeviceQuarantineInfoAsync(
                "https://vault.example",
                Email,
                TestContext.Current.CancellationToken));

        Assert.False(found.IsClearEligible);
        Assert.Equal(
            LoginWithDeviceQuarantineClearStatus.NotEligible,
            (await fixture.Auth.ClearLoginWithDeviceQuarantineAsync(
                "https://vault.example",
                Email,
                found,
                TestContext.Current.CancellationToken)).Status);
        clock.Advance(TimeSpan.FromMinutes(6));
        var eligible = Assert.IsType<LoginWithDeviceQuarantineInfo.Found>(
            await fixture.Auth.GetLoginWithDeviceQuarantineInfoAsync(
                "https://vault.example",
                Email,
                TestContext.Current.CancellationToken));
        Assert.True(eligible.IsClearEligible);

        var cleared = await fixture.Auth.ClearLoginWithDeviceQuarantineAsync(
            "https://vault.example",
            Email,
            eligible,
            TestContext.Current.CancellationToken);

        Assert.True(cleared.IsCleared);
        Assert.Null(store.Current);
        Assert.IsType<LoginWithDeviceQuarantineInfo.Absent>(
            await fixture.Auth.GetLoginWithDeviceQuarantineInfoAsync(
                "https://vault.example",
                Email,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManualClear_WhenSnapshotChangedDoesNotDeleteReplacement()
    {
        var clock = new ControllableTimeProvider(Start + TimeSpan.FromMinutes(10));
        var store = new RecordingLoginWithDeviceQuarantineStore(clock);
        store.Seed(QuarantineRecord());
        var fixture = CreateFixture(
            new ControlledLoginWithDeviceService(clock),
            new RecordingIdentityApiClient(),
            timeProvider: clock,
            quarantineStore: store);
        var stale = Assert.IsType<LoginWithDeviceQuarantineInfo.Found>(
            await fixture.Auth.GetLoginWithDeviceQuarantineInfoAsync(
                "https://vault.example",
                Email,
                TestContext.Current.CancellationToken));
        var replacement = QuarantineRecord(
            entryId: Guid.Parse("55555555-5555-5555-5555-555555555555"),
            revision: 2);
        store.Seed(replacement);

        var result = await fixture.Auth.ClearLoginWithDeviceQuarantineAsync(
            "https://vault.example",
            Email,
            stale,
            TestContext.Current.CancellationToken);

        Assert.Equal(LoginWithDeviceQuarantineClearStatus.Changed, result.Status);
        Assert.Same(replacement, store.Current);
    }

    [Fact]
    public async Task ManualClear_WhenStorageFailsRemainsBlocked()
    {
        var clock = new ControllableTimeProvider(Start + TimeSpan.FromMinutes(10));
        var store = new RecordingLoginWithDeviceQuarantineStore(clock)
        {
            RemoveException = new IOException("replace failed"),
        };
        var record = QuarantineRecord();
        store.Seed(record);
        var fixture = CreateFixture(
            new ControlledLoginWithDeviceService(clock),
            new RecordingIdentityApiClient(),
            timeProvider: clock,
            quarantineStore: store);
        var found = Assert.IsType<LoginWithDeviceQuarantineInfo.Found>(
            await fixture.Auth.GetLoginWithDeviceQuarantineInfoAsync(
                "https://vault.example",
                Email,
                TestContext.Current.CancellationToken));

        var result = await fixture.Auth.ClearLoginWithDeviceQuarantineAsync(
            "https://vault.example",
            Email,
            found,
            TestContext.Current.CancellationToken);

        Assert.Equal(LoginWithDeviceQuarantineClearStatus.Unavailable, result.Status);
        Assert.Same(record, store.Current);
        Assert.False(await fixture.Auth.IsLoginWithDeviceAvailableAsync(
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(QuarantineBindingMismatch.Endpoints)]
    [InlineData(QuarantineBindingMismatch.Email)]
    [InlineData(QuarantineBindingMismatch.Device)]
    public async Task QuarantineForDifferentCompleteBinding_DoesNotBlockAvailability(
        QuarantineBindingMismatch mismatch)
    {
        var record = mismatch switch
        {
            QuarantineBindingMismatch.Endpoints => QuarantineRecord(
                endpoints: ServerEndpoints.BitwardenUs),
            QuarantineBindingMismatch.Email => QuarantineRecord(
                email: "other@example.com"),
            QuarantineBindingMismatch.Device => QuarantineRecord(
                deviceIdentifier: "66666666-6666-6666-6666-666666666666"),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };
        var store = new RecordingLoginWithDeviceQuarantineStore();
        store.Seed(record);
        var fixture = CreateFixture(
            new ControlledLoginWithDeviceService(),
            new RecordingIdentityApiClient(),
            quarantineStore: store);

        Assert.True(await fixture.Auth.IsLoginWithDeviceAvailableAsync(
            "https://vault.example",
            Email,
            TestContext.Current.CancellationToken));
        Assert.Same(record, store.Current);
    }

    [Fact]
    public async Task OnlineMasterPasswordSuccess_ClearsMatchingExactMarker()
    {
        var store = new RecordingLoginWithDeviceQuarantineStore();
        store.Seed(QuarantineRecord());
        var protectedUserKey = CreatePasswordProtectedUserKey(MasterPassword);
        var identity = new RecordingIdentityApiClient
        {
            PreloginResult = Pbkdf2Prelogin(),
        };
        identity.Enqueue(new ConnectTokenResult.Success(V1Token(protectedUserKey)));
        var fixture = CreateFixture(
            new ControlledLoginWithDeviceService(),
            identity,
            quarantineStore: store);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        var result = await fixture.Auth.LoginAsync(
            attempt,
            "https://vault.example",
            Email,
            MasterPassword,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.Null(store.Current);
        Assert.Contains(
            LoginWithDeviceQuarantineRemovalReason.SessionPersisted,
            store.RemovalReasons);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlineSsoSuccess_ClearsOnlyWhenTokenProvesEmailBinding(
        bool includeEmailClaim)
    {
        var store = new RecordingLoginWithDeviceQuarantineStore();
        store.Seed(QuarantineRecord());
        var protectedUserKey = CreatePasswordProtectedUserKey(MasterPassword);
        var identity = new RecordingIdentityApiClient
        {
            PreloginResult = Pbkdf2Prelogin(),
        };
        identity.Enqueue(new ConnectTokenResult.Success(V1Token(
            protectedUserKey,
            accessToken: AccessToken(includeEmailClaim))));
        var fixture = CreateFixture(
            new ControlledLoginWithDeviceService(),
            identity,
            quarantineStore: store);
        var attempt = fixture.Auth.BeginAuthenticationAttempt();

        var result = await fixture.Auth.LoginWithSsoAsync(
            attempt,
            "https://vault.example",
            Email,
            new SsoAuthorizationCode(
                "sso-code",
                SsoVerifier,
                "http://localhost:8065",
                SsoVerifier,
                "organization"),
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.SsoAuthenticated>(result);
        if (includeEmailClaim)
            Assert.Null(store.Current);
        else
            Assert.NotNull(store.Current);
    }

    [Fact]
    public async Task CompleteTrustedDeviceApprovalWithoutPending_FailsClosedAndZerosInput()
    {
        var loginWithDevice = new ControlledLoginWithDeviceService();
        var fixture = CreateFixture(loginWithDevice, new RecordingIdentityApiClient());
        fixture.TokenStore.Save(SavedSession());
        var lease = fixture.Session.RestorePersistedSession(SavedSession());
        var supplied = Enumerable.Range(1, 64).Select(value => (byte)value).ToArray();

        var result = await fixture.Auth.CompleteTrustedDeviceApprovalAsync(
            lease,
            supplied,
            trustCurrentDevice: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AuthFailureKind.SessionExpired,
            Assert.IsType<AuthResult.Failure>(result).Kind);
        Assert.All(supplied, value => Assert.Equal(0, value));
        Assert.Null(fixture.Session.UserKey);
    }

    private static TestFixture CreateFixture(
        ControlledLoginWithDeviceService loginWithDevice,
        RecordingIdentityApiClient identity,
        TimeProvider? timeProvider = null,
        SyncResponse? sync = null,
        FakeCacheStore? cacheStore = null,
        ITokenStore? tokenStore = null,
        RecordingLoginWithDeviceQuarantineStore? quarantineStore = null,
        RecordingPublicAuthRequestApiClient? publicAuthRequest = null,
        Func<CancellationToken, Task<SyncResponse>>? syncHandler = null,
        IAccountDeletionTombstoneStore? accountDeletionTombstones = null)
    {
        var session = new VaultSession();
        tokenStore ??= new MemoryTokenStore();
        quarantineStore ??= new RecordingLoginWithDeviceQuarantineStore(
            timeProvider);
        publicAuthRequest ??= new RecordingPublicAuthRequestApiClient();
        var crypto = new CryptoService();
        var decryptor = new VaultDecryptor(crypto);
        var cache = new VaultCacheService(cacheStore ?? new FakeCacheStore(), decryptor, session);
        var read = new ControlledReadonlyApiClient(
            syncHandler ?? (_ => Task.FromResult(sync ?? Sync())))
        {
            GetDevices = _ => Task.FromResult(new ListResponse<DeviceDto>([], null, "list")),
        };
        var clients = new FakeVaultApiClientProvider()
            .AddAnonymous<IIdentityApiClient>(identity)
            .AddAnonymous<IPublicAuthRequestApiClient>(publicAuthRequest)
            .AddAuthenticated<IReadonlyApiClient>(read);
        var bootstrapper = new VaultBootstrapper(
            new SyncService(clients, decryptor, session, cache),
            clients,
            session);
        var refresher = new TokenRefreshService(clients, session, tokenStore);
        var auth = new AuthService(
            clients,
            crypto,
            session,
            tokenStore,
            bootstrapper,
            cache,
            refresher,
            deviceIdentifierProvider: new FixedDeviceIdentifierProvider(DeviceId),
            timeProvider: timeProvider,
            loginWithDevice: loginWithDevice,
            loginWithDeviceQuarantineStore: quarantineStore,
            accountDeletionTombstones: accountDeletionTombstones);
        return new TestFixture(
            auth,
            session,
            tokenStore,
            quarantineStore,
            publicAuthRequest);
    }

    private static LoginWithDevicePollResult ApprovedGrant(
        byte[] userKey,
        int securityVersion) => new(
            LoginWithDevicePollStatus.Approved,
            new LoginWithDeviceGrant(
                RequestId,
                AccessCode,
                Email,
                ServerEndpoints.FromWebVault("https://vault.example"),
                DeviceId,
                userKey,
                securityVersion));

    private static TokenResponse V1Token(
        string protectedUserKey,
        string? twoFactorToken = null,
        string? accessToken = null) => new(
            accessToken ?? AccessToken(),
            "refresh",
            3600,
            "Bearer",
            "api offline_access",
            protectedUserKey,
            null,
            KdfType.Pbkdf2,
            600_000,
            TwoFactorToken: twoFactorToken);

    private static SyncResponse Sync(AccountKeysResponse? accountKeys = null)
    {
        JsonElement? accountKeysJson = accountKeys is null
            ? null
            : JsonSerializer.SerializeToElement(
                accountKeys,
                ApiJsonContext.Default.AccountKeysResponse);
        return new SyncResponse(
            "sync",
            new ProfileDto(AccountId, Email, "Me", null, null)
            {
                AccountKeys = accountKeysJson,
                SecurityStamp = "stamp-1",
            },
            [],
            []);
    }

    private static AccountKeysResponse ToAccountKeysResponse(
        V2AccountKeyMaterial material) => new(
            new PublicKeyEncryptionKeyPairResponse(
                material.WrappedPrivateKey,
                material.AccountPublicKey,
                material.SignedPublicKey),
            new SignatureKeyPairResponse(
                material.WrappedSigningKey,
                material.VerifyingKey),
            new AccountSecurityState(
                material.SecurityState,
                material.SecurityVersion));

    private static string CreateProtectedUserKey(byte[] userKey)
    {
        using var wrappingKey = new SymmetricCryptoKey(
            Enumerable.Range(128, 64).Select(value => (byte)value).ToArray());
        return new CryptoService().Encrypt(userKey, wrappingKey).ToString();
    }

    private static string CreatePasswordProtectedUserKey(string password)
    {
        var crypto = new CryptoService();
        using var userKey = new SymmetricCryptoKey(
            Enumerable.Range(32, 64).Select(value => (byte)value).ToArray());
        var masterKey = crypto.DeriveMasterKey(
            password,
            Email,
            KdfType.Pbkdf2,
            600_000,
            null,
            null);
        try
        {
            using var stretchedKey = crypto.StretchMasterKey(masterKey);
            return crypto.Encrypt(userKey.FullKey, stretchedKey).ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private static string CreatePasswordProtectedV2UserKey(V2UserKey userKey)
    {
        var crypto = new CryptoService();
        var masterKey = crypto.DeriveMasterKey(
            MasterPassword,
            Email,
            KdfType.Pbkdf2,
            600_000,
            null,
            null);
        byte[]? transport = null;
        try
        {
            using var stretchedKey = crypto.StretchMasterKey(masterKey);
            transport = userKey.ExportTransport();
            return crypto.Encrypt(transport, stretchedKey).ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            if (transport is not null)
                CryptographicOperations.ZeroMemory(transport);
        }
    }

    private static PreloginResponse Pbkdf2Prelogin() => new(
        KdfType.Pbkdf2,
        600_000,
        null,
        null);

    private static string AccessToken(bool includeEmailClaim = true)
    {
        static string Encode(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        var header = Encode("""{"alg":"none"}""");
        var payload = Encode(includeEmailClaim
            ? $$"""{"sub":"{{AccountId}}","email":"{{Email}}"}"""
            : $$"""{"sub":"{{AccountId}}"}""");
        return $"{header}.{payload}.signature";
    }

    private static PersistedSession SavedSession(
        ServerEndpoints? endpoints = null,
        string email = Email,
        string deviceIdentifier = DeviceId,
        string? twoFactorToken = null) => new(
            PersistedSession.CurrentSchemaVersion,
            AccountId,
            endpoints ?? ServerEndpoints.FromWebVault("https://vault.example"),
            7,
            email,
            deviceIdentifier,
            "old-refresh",
            "2.AA==|AA==|AA==",
            KdfType.Pbkdf2,
            600_000,
            null,
            null)
        {
            TwoFactorToken = twoFactorToken,
        };

    private static LoginWithDeviceQuarantineRecord QuarantineRecord(
        Guid? entryId = null,
        long revision = 1,
        ServerEndpoints? endpoints = null,
        string email = Email,
        string deviceIdentifier = DeviceId) => new(
            entryId ?? Guid.Parse("44444444-4444-4444-4444-444444444444"),
            revision,
            new LoginWithDeviceQuarantineBinding(
                endpoints ?? ServerEndpoints.FromWebVault("https://vault.example"),
                email,
                deviceIdentifier),
            RequestId,
            Start,
            Start + TimeSpan.FromMinutes(5),
            Start + TimeSpan.FromMinutes(6));

    private sealed record TestFixture(
        AuthService Auth,
        VaultSession Session,
        ITokenStore TokenStore,
        RecordingLoginWithDeviceQuarantineStore QuarantineStore,
        RecordingPublicAuthRequestApiClient PublicAuthRequest);

    public enum RememberBindingMismatch
    {
        Email,
        Device,
        Origin,
        EmptyToken,
    }

    public enum QuarantineBindingMismatch
    {
        Endpoints,
        Email,
        Device,
    }

    private sealed class ThrowingReadTokenStore : ITokenStore
    {
        public PersistedSession? LastSaved { get; private set; }

        public bool TryLoad(out PersistedSession session) =>
            throw new IOException("Persisted authentication state is unavailable.");

        public void Save(PersistedSession session) => LastSaved = session;

        public void Clear() => LastSaved = null;
    }

    private sealed class ThrowingWriteTokenStore : ITokenStore
    {
        public bool TryLoad(out PersistedSession session)
        {
            session = null!;
            return false;
        }

        public void Save(PersistedSession session) =>
            throw new IOException("session persistence unavailable");

        public void Clear()
        {
        }
    }

    private sealed class CallbackTokenStore(
        Action? onSave = null,
        bool rejectRemove = false,
        Exception? removeException = null) : ITokenStore
    {
        private readonly object _gate = new();
        private PersistedSession? _session;

        public int RemoveCount { get; private set; }

        public bool HasSession
        {
            get
            {
                lock (_gate)
                    return _session is not null;
            }
        }

        public bool TryLoad(out PersistedSession session)
        {
            lock (_gate)
            {
                session = _session!;
                return _session is not null;
            }
        }

        public void Save(PersistedSession session)
        {
            lock (_gate)
            {
                _session = session;
                onSave?.Invoke();
            }
        }

        public void Clear()
        {
            lock (_gate)
                _session = null;
        }

        public bool Remove(string accountId, ServerEndpoints endpoints)
        {
            lock (_gate)
            {
                RemoveCount++;
                if (removeException is not null)
                    throw removeException;
                if (rejectRemove)
                    return false;
                if (_session is { } persisted
                    && persisted.AccountId == accountId
                    && persisted.Endpoints == endpoints)
                {
                    _session = null;
                }
                return true;
            }
        }
    }

    private sealed class FixedDeviceIdentifierProvider(string identifier)
        : IDeviceIdentifierProvider
    {
        public string GetDeviceIdentifier() => identifier;
    }

    private sealed class ControlledLoginWithDeviceService : ILoginWithDeviceService
    {
        private readonly Queue<Func<CancellationToken, Task<LoginWithDevicePollResult>>> _polls = [];
        private readonly TimeProvider _timeProvider;

        public ControlledLoginWithDeviceService(TimeProvider? timeProvider = null)
        {
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public int BeginCount { get; private set; }
        public int PollCount { get; private set; }
        public int CancelCount { get; private set; }

        public void EnqueuePoll(LoginWithDevicePollResult result) =>
            EnqueuePoll(_ => Task.FromResult(result));

        public void EnqueuePoll(
            Func<CancellationToken, Task<LoginWithDevicePollResult>> poll) =>
            _polls.Enqueue(poll);

        public Task<LoginWithDeviceBeginResult> BeginAsync(
            AuthenticationAttemptLease attempt,
            ServerEndpoints endpoints,
            string email,
            string deviceIdentifier,
            CancellationToken ct = default)
        {
            BeginCount++;
            return Task.FromResult(new LoginWithDeviceBeginResult(
                RequestId,
                "one-two-three",
                _timeProvider.GetUtcNow() + TimeSpan.FromMinutes(5)));
        }

        public Task<LoginWithDevicePollResult> PollAsync(
            AuthenticationAttemptLease attempt,
            CancellationToken ct = default)
        {
            PollCount++;
            return _polls.Dequeue()(ct);
        }

        public void Cancel(AuthenticationAttemptLease attempt) => CancelCount++;
    }

    private sealed class RecordingIdentityApiClient : IIdentityApiClient
    {
        private readonly Queue<Func<CancellationToken, Task<ConnectTokenResult>>> _results = [];

        public List<ConnectTokenRequest> TokenRequests { get; } = [];
        public List<SendEmailLoginRequest> EmailRequests { get; } = [];
        public Action? OnConnectToken { get; set; }
        public PreloginResponse? PreloginResult { get; set; }

        public void Enqueue(ConnectTokenResult result) =>
            Enqueue(_ => Task.FromResult(result));

        public void Enqueue(Func<CancellationToken, Task<ConnectTokenResult>> result) =>
            _results.Enqueue(result);

        public Task<ConfigResponse> GetConfigAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PreloginResponse> PreloginAsync(
            string email,
            CancellationToken ct = default) => Task.FromResult(
                PreloginResult ?? throw new NotSupportedException());

        public Task<ConnectTokenResult> ConnectTokenAsync(
            ConnectTokenRequest request,
            CancellationToken ct = default)
        {
            OnConnectToken?.Invoke();
            TokenRequests.Add(request);
            return _results.Dequeue()(ct);
        }

        public Task SendTwoFactorEmailLoginAsync(
            SendEmailLoginRequest request,
            CancellationToken ct = default)
        {
            EmailRequests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPublicAuthRequestApiClient
        : IPublicAuthRequestApiClient
    {
        public bool IsKnownDevice { get; set; } = true;
        public int KnownDeviceCount { get; private set; }

        public Task<bool> GetKnownDeviceAsync(
            string email,
            string deviceIdentifier,
            CancellationToken ct = default)
        {
            KnownDeviceCount++;
            return Task.FromResult(IsKnownDevice);
        }

        public Task<AuthRequestResponse> CreateAsync(
            AuthRequestRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<AuthRequestResponse> GetResponseAsync(
            string id,
            string code,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingLoginWithDeviceQuarantineStore(
        TimeProvider? timeProvider = null,
        List<string>? events = null) : ILoginWithDeviceQuarantineStore
    {
        private readonly object _gate = new();
        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
        private LoginWithDeviceQuarantineRecord? _record;
        private long _revision;

        public LoginWithDeviceQuarantineStoreReadStatus? ForcedReadStatus { get; set; }
        public Exception? ArmException { get; set; }
        public Exception? RemoveException { get; set; }
        public bool RejectArm { get; set; }
        public bool RejectRemove { get; set; }
        public int ReadCount { get; private set; }
        public int ArmCount { get; private set; }
        public int RemoveCount { get; private set; }
        public List<string> Events { get; } = events ?? [];
        public List<LoginWithDeviceQuarantineRemovalReason> RemovalReasons { get; } = [];

        public LoginWithDeviceQuarantineRecord? Current
        {
            get
            {
                lock (_gate)
                    return _record;
            }
        }

        public void Seed(LoginWithDeviceQuarantineRecord record)
        {
            lock (_gate)
            {
                _record = record;
                _revision = Math.Max(_revision, record.Revision);
            }
        }

        public LoginWithDeviceQuarantineStoreReadResult Read(
            LoginWithDeviceQuarantineBinding binding)
        {
            lock (_gate)
            {
                ReadCount++;
                Events.Add("read");
                if (ForcedReadStatus is { } forced)
                {
                    return forced switch
                    {
                        LoginWithDeviceQuarantineStoreReadStatus.Absent =>
                            LoginWithDeviceQuarantineStoreReadResult.Absent,
                        LoginWithDeviceQuarantineStoreReadStatus.Corrupt =>
                            LoginWithDeviceQuarantineStoreReadResult.Corrupt,
                        LoginWithDeviceQuarantineStoreReadStatus.Unavailable =>
                            LoginWithDeviceQuarantineStoreReadResult.Unavailable,
                        LoginWithDeviceQuarantineStoreReadStatus.Found
                            when _record is not null =>
                            LoginWithDeviceQuarantineStoreReadResult.Found(_record),
                        _ => LoginWithDeviceQuarantineStoreReadResult.Corrupt,
                    };
                }

                return _record?.Binding == binding
                    ? LoginWithDeviceQuarantineStoreReadResult.Found(_record)
                    : LoginWithDeviceQuarantineStoreReadResult.Absent;
            }
        }

        public LoginWithDeviceQuarantineRecord? TryArm(
            LoginWithDeviceQuarantineBinding binding,
            string requestId,
            DateTimeOffset armedAt,
            DateTimeOffset requestExpiresAt,
            DateTimeOffset clearEligibleAt)
        {
            lock (_gate)
            {
                ArmCount++;
                Events.Add("arm");
                if (ArmException is not null)
                    throw ArmException;
                if (RejectArm || ForcedReadStatus is not null || _record?.Binding == binding)
                    return null;

                _record = new LoginWithDeviceQuarantineRecord(
                    Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    ++_revision,
                    binding,
                    requestId,
                    armedAt,
                    requestExpiresAt,
                    clearEligibleAt);
                return _record;
            }
        }

        public bool Remove(
            LoginWithDeviceQuarantineRecord expected,
            LoginWithDeviceQuarantineRemovalReason reason)
        {
            lock (_gate)
            {
                RemoveCount++;
                Events.Add("remove");
                RemovalReasons.Add(reason);
                if (RemoveException is not null)
                    throw RemoveException;
                if (RejectRemove || _record != expected)
                    return false;
                if (reason == LoginWithDeviceQuarantineRemovalReason.UserConfirmedAfterEligibility
                    && !_record.IsClearEligible(_timeProvider.GetUtcNow()))
                {
                    return false;
                }

                _record = null;
                return true;
            }
        }
    }

    private sealed class ControllableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ControllableTimer> _timers = [];
        private DateTimeOffset _now = start;
        private int _getsBeforeDeferredAdvance = -1;
        private TimeSpan _deferredAdvance;

        public override DateTimeOffset GetUtcNow()
        {
            ControllableTimer[] timers = [];
            DateTimeOffset now;
            lock (_gate)
            {
                if (_getsBeforeDeferredAdvance == 0)
                {
                    _getsBeforeDeferredAdvance = -1;
                    _now += _deferredAdvance;
                    timers = _timers.ToArray();
                }
                else if (_getsBeforeDeferredAdvance > 0)
                {
                    _getsBeforeDeferredAdvance--;
                }
                now = _now;
            }
            foreach (var timer in timers)
                timer.FireIfDue(now);
            return now;
        }

        public void DeferAdvanceUntilGet(
            int callsBeforeAdvance,
            TimeSpan duration)
        {
            if (callsBeforeAdvance < 0)
                throw new ArgumentOutOfRangeException(nameof(callsBeforeAdvance));
            lock (_gate)
            {
                _getsBeforeDeferredAdvance = callsBeforeAdvance;
                _deferredAdvance = duration;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ControllableTimer(this, callback, state, dueTime, period);
            lock (_gate)
                _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            ControllableTimer[] timers;
            lock (_gate)
            {
                _now += duration;
                timers = _timers.ToArray();
            }
            foreach (var timer in timers)
                timer.FireIfDue(_now);
        }

        private sealed class ControllableTimer : ITimer
        {
            private readonly ControllableTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private readonly object _gate = new();
            private DateTimeOffset _dueAt;
            private TimeSpan _period;
            private bool _disposed;

            public ControllableTimer(
                ControllableTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                _dueAt = owner.GetUtcNow() + dueTime;
                _period = period;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_gate)
                {
                    if (_disposed)
                        return false;
                    _dueAt = _owner.GetUtcNow() + dueTime;
                    _period = period;
                    return true;
                }
            }

            public void FireIfDue(DateTimeOffset now)
            {
                lock (_gate)
                {
                    if (_disposed || now < _dueAt)
                        return;
                    _dueAt = _period == Timeout.InfiniteTimeSpan
                        ? DateTimeOffset.MaxValue
                        : now + _period;
                }
                _callback(_state);
            }

            public void Dispose()
            {
                lock (_gate)
                    _disposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
