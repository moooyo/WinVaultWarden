using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class AuthServiceApiKeyTests
{
    private const string AccountId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ClientId = "user." + AccountId;
    private const string Email = "me@example.com";
    private const string Password = "correct horse battery staple";
    private const string ApiSecret = "synthetic-personal-api-secret";
    private const int Iterations = 600_000;
    private const string Server = "https://vault.example";

    [Fact]
    public async Task PersonalApiKeyAuthenticatesAndBootstrapsAV2AccountThroughTheActualHttpContract()
    {
        using var material = V2AccountKeyMaterial.Generate();
        using var fixture = new Fixture();
        var protectedUserKey = ProtectV2UserKey(material.UserKey);
        fixture.EnqueueSuccessfulLogin(
            customTokenJson: V2TokenJson(material, protectedUserKey),
            customSyncJson: V2SyncJson(material));

        var result = await fixture.LoginAsync();

        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal(VaultState.Unlocked, fixture.Session.State);
        Assert.Equal(2, fixture.Session.SecurityVersion);
        Assert.True(fixture.Session.UsesV2UserKey);
        Assert.Null(fixture.Session.UserKey);
        Assert.Equal(material.WrappedPrivateKey, fixture.Session.EncryptedPrivateKey);
        Assert.True(fixture.Store.TryLoad(out var persisted));
        Assert.True(persisted.IsApiKeySession);
        Assert.True(persisted.HasValidBinding);
        Assert.Empty(persisted.RefreshToken);
        Assert.Equal(2, persisted.UserKeySecurityVersion);
        Assert.Equal(material.UserKey.KeyIdHex, persisted.UserKeyId);
        Assert.Equal(protectedUserKey, persisted.ProtectedUserKey);
        var state = Assert.IsType<PersistedAccountCryptographicState>(persisted.AccountCryptographicState);
        Assert.Equal(material.WrappedPrivateKey, state.WrappedPrivateKey);
        Assert.Equal(material.WrappedSigningKey, state.WrappedSigningKey);
        Assert.Equal(material.SecurityState, state.SignedSecurityState);
        Assert.Equal(3, fixture.Handler.Requests.Count);
        Assert.DoesNotContain(Password, fixture.Handler.Bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain(ApiSecret, JsonSerializer.Serialize(persisted), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersonalApiKeyRejectsAV2AccountWhoseSignedSecurityStateWasTampered()
    {
        using var material = V2AccountKeyMaterial.Generate();
        using var fixture = new Fixture();
        var protectedUserKey = ProtectV2UserKey(material.UserKey);
        var validKeys = ToAccountKeysResponse(material);
        var encodedState = Convert.FromBase64String(material.SecurityState);
        string tamperedState;
        try
        {
            encodedState[^1] ^= 0x01;
            tamperedState = Convert.ToBase64String(encodedState);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedState);
        }
        var tamperedKeys = validKeys with
        {
            SecurityState = validKeys.SecurityState! with { SecurityState = tamperedState },
        };
        fixture.Handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            V2TokenJson(material, protectedUserKey, tamperedKeys)));

        var result = await fixture.LoginAsync();

        Assert.IsType<AuthResult.Failure>(result);
        AssertUnestablished(fixture);
        Assert.False(fixture.Session.UsesV2UserKey);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task PersonalApiKeyWithoutRefreshTokenAuthenticatesAndBootstrapsWithoutStoringTheSecret()
    {
        using var fixture = new Fixture();
        fixture.EnqueueSuccessfulLogin();

        var result = await fixture.LoginAsync();

        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal(VaultState.Unlocked, fixture.Session.State);
        Assert.Equal(AccountId, fixture.Session.CaptureLease().AccountId);
        Assert.Equal(fixture.UserKey.FullKey, fixture.Session.UserKey!.FullKey);
        Assert.Equal(AccessToken(), fixture.Session.AccessToken);
        Assert.True(string.IsNullOrEmpty(fixture.Session.RefreshToken));
        Assert.True(fixture.Store.TryLoad(out var persisted));
        Assert.True(persisted.IsApiKeySession);
        Assert.True(persisted.HasValidBinding);
        Assert.True(string.IsNullOrEmpty(persisted.RefreshToken));
        Assert.Equal(fixture.ProtectedUserKey, persisted.ProtectedUserKey);
        Assert.Equal(Email, persisted.MasterPasswordSalt);
        var storedJson = JsonSerializer.Serialize(persisted);
        Assert.DoesNotContain(ApiSecret, storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, storedJson, StringComparison.Ordinal);
        var restored = Assert.IsType<PersistedSession>(JsonSerializer.Deserialize<PersistedSession>(storedJson));
        Assert.True(restored.IsApiKeySession);
        Assert.True(restored.HasValidBinding);
        Assert.False((restored with { IsApiKeySession = false }).HasValidBinding);
        Assert.False((restored with { UnlockMethod = PersistedSessionUnlockMethod.TrustedDeviceSso }).HasValidBinding);
        Assert.Equal(3, fixture.Handler.Requests.Count);
        Assert.Equal("/identity/connect/token", fixture.Handler.Requests[0].RequestUri!.AbsolutePath);
        var form = ParseForm(fixture.Handler.Bodies[0]);
        Assert.Equal("client_credentials", form["grant_type"]);
        Assert.Equal(ClientId, form["client_id"]);
        Assert.Equal(ApiSecret, form["client_secret"]);
        Assert.False(form.ContainsKey("password"));
        Assert.False(form.ContainsKey("username"));
        Assert.False(form.ContainsKey("refresh_token"));
        Assert.DoesNotContain(Password, fixture.Handler.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongMasterPasswordDoesNotEstablishOrPersistAnApiKeySession()
    {
        using var fixture = new Fixture();
        fixture.Handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            fixture.TokenJson()));

        var result = await fixture.LoginAsync(masterPassword: "incorrect-local-password");

        Assert.IsType<AuthResult.Failure>(result);
        AssertUnestablished(fixture);
        Assert.Single(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee", Email)]
    [InlineData(AccountId, "different@example.com")]
    public async Task TokenIdentityMustMatchBothThePersonalClientIdAndTheEnteredAccount(
        string tokenAccountId,
        string tokenEmail)
    {
        using var fixture = new Fixture();
        fixture.Handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            fixture.TokenJson(tokenAccountId: tokenAccountId, tokenEmail: tokenEmail)));

        var result = await fixture.LoginAsync();

        Assert.IsType<AuthResult.Failure>(result);
        AssertUnestablished(fixture);
        Assert.Single(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("api.organization")]
    [InlineData("api offline_access")]
    public async Task UnexpectedTokenScopesCannotCreateAPersonalApiSession(string scope)
    {
        using var fixture = new Fixture();
        fixture.Handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            fixture.TokenJson(scope: scope)));

        var result = await fixture.LoginAsync();

        Assert.IsType<AuthResult.Failure>(result);
        AssertUnestablished(fixture);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task ServerRejectionDoesNotExposeDiagnosticsOrRetainTheApiSecret()
    {
        using var fixture = new Fixture();
        fixture.Handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_client","error_description":"sensitive authentication diagnostic"}"""));

        var result = await fixture.LoginAsync();

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.DoesNotContain("sensitive authentication diagnostic", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiSecret, failure.ToString(), StringComparison.Ordinal);
        AssertUnestablished(fixture);
    }

    [Theory]
    [InlineData("organization.aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("user.not-a-guid")]
    public async Task OrganizationAndMalformedClientIdsDoNotReachTheNetwork(string clientId)
    {
        using var fixture = new Fixture();

        var result = await fixture.LoginAsync(clientId: clientId);

        Assert.IsType<AuthResult.Failure>(result);
        Assert.Empty(fixture.Handler.Requests);
        AssertUnestablished(fixture);
    }

    [Fact]
    public async Task APreCancelledLoginDoesNotDispatchOrRetainCredentials()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.LoginAsync(ct: cancellation.Token));

        Assert.Empty(fixture.Handler.Requests);
        AssertUnestablished(fixture);
    }

    [Fact]
    public async Task AStaleTokenResponseCannotPromoteAnAbandonedAttempt()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Enqueue((_, _) =>
        {
            started.TrySetResult();
            return response.Task;
        });
        var attempt = fixture.Service.BeginAuthenticationAttempt();
        var pending = fixture.Service.LoginWithApiKeyAsync(
            attempt,
            Server,
            Email,
            ClientId,
            ApiSecret,
            Password,
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        fixture.Service.AbandonAuthenticationAttempt(attempt);
        response.TrySetResult(FakeHttpMessageHandler.Json(HttpStatusCode.OK, fixture.TokenJson()));

        var result = await pending.WaitAsync(TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Failure>(result);
        AssertUnestablished(fixture);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task ApiKeyLoginCanUnlockItsEncryptedCacheWithoutPersistingOrResendingTheApiKey()
    {
        using var fixture = new Fixture();
        fixture.EnqueueSuccessfulLogin();
        Assert.IsType<AuthResult.Success>(await fixture.LoginAsync());
        await fixture.Service.LockAsync(TestContext.Current.CancellationToken);

        var result = await fixture.Service.UnlockAsync(
            fixture.Service.BeginAuthenticationAttempt(),
            Password,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal(VaultState.Unlocked, fixture.Session.State);
        Assert.Equal(fixture.UserKey.FullKey, fixture.Session.UserKey!.FullKey);
        Assert.Null(fixture.Session.AccessToken);
        Assert.True(string.IsNullOrEmpty(fixture.Session.RefreshToken));
        Assert.Equal(3, fixture.Handler.Requests.Count);
        Assert.True(fixture.Store.TryLoad(out var persisted));
        Assert.True(persisted.IsApiKeySession);
    }

    private static void AssertUnestablished(Fixture fixture)
    {
        Assert.Null(fixture.Session.UserKey);
        Assert.Null(fixture.Session.AccessToken);
        Assert.Equal(VaultState.LoggedOut, fixture.Session.State);
        Assert.False(fixture.Store.TryLoad(out _));
    }

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));

    private static string AccessToken(string accountId = AccountId, string email = Email)
    {
        static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Encode("{\"alg\":\"none\"}")}.{Encode(JsonSerializer.Serialize(new { sub = accountId, email }))}.signature";
    }

    private static string ProtectV2UserKey(V2UserKey userKey)
    {
        var crypto = new CryptoService();
        var masterKey = crypto.DeriveMasterKey(Password, Email, KdfType.Pbkdf2, Iterations, null, null);
        byte[]? transport = null;
        try
        {
            using var stretched = crypto.StretchMasterKey(masterKey);
            transport = userKey.ExportTransport();
            return crypto.Encrypt(transport, stretched).ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            if (transport is not null)
                CryptographicOperations.ZeroMemory(transport);
        }
    }

    private static AccountKeysResponse ToAccountKeysResponse(V2AccountKeyMaterial material) => new(
        new PublicKeyEncryptionKeyPairResponse(material.WrappedPrivateKey, material.AccountPublicKey, material.SignedPublicKey),
        new SignatureKeyPairResponse(material.WrappedSigningKey, material.VerifyingKey),
        new AccountSecurityState(material.SecurityState, material.SecurityVersion));

    private static string V2TokenJson(
        V2AccountKeyMaterial material,
        string protectedUserKey,
        AccountKeysResponse? accountKeysOverride = null)
    {
        var token = new TokenResponse(
            AccessToken(),
            string.Empty,
            3600,
            "Bearer",
            "api",
            protectedUserKey,
            null,
            KdfType.Pbkdf2,
            Iterations,
            UserDecryptionOptions: new UserDecryptionOptionsResponse(
                true,
                new MasterPasswordUnlockResponse(
                    new PreloginKdfSettingsResponse(KdfType.Pbkdf2, Iterations, null, null),
                    null,
                    protectedUserKey,
                    Email,
                    material.UserKey.KeyIdHex)),
            AccountKeys: accountKeysOverride ?? ToAccountKeysResponse(material));
        var node = JsonSerializer.SerializeToNode(token, ApiJsonContext.Default.TokenResponse)!.AsObject();
        Assert.True(node.Remove("refresh_token"));
        return node.ToJsonString();
    }

    private static string V2SyncJson(V2AccountKeyMaterial material)
    {
        var accountKeys = JsonSerializer.SerializeToElement(
            ToAccountKeysResponse(material),
            ApiJsonContext.Default.AccountKeysResponse);
        return JsonSerializer.Serialize(new SyncResponse(
            "sync",
            new ProfileDto(AccountId, Email, "V2 API User", null, null)
            {
                AccountKeys = accountKeys,
                SecurityStamp = "synthetic-v2-api-stamp",
            },
            [],
            []), ApiJsonContext.Default.SyncResponse);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        public FakeHttpMessageHandler Handler { get; } = new();
        public VaultSession Session { get; } = new();
        public MemoryTokenStore Store { get; } = new();
        public SymmetricCryptoKey UserKey { get; }
        public string ProtectedUserKey { get; }
        public AuthService Service { get; }

        public Fixture()
        {
            var crypto = new CryptoService();
            UserKey = new SymmetricCryptoKey(Enumerable.Range(64, 64).Select(value => (byte)value).ToArray());
            var masterKey = crypto.DeriveMasterKey(Password, Email, KdfType.Pbkdf2, Iterations, null, null);
            try
            {
                using var stretched = crypto.StretchMasterKey(masterKey);
                ProtectedUserKey = crypto.Encrypt(UserKey.FullKey, stretched).ToString();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
            _http = new HttpClient(Handler);
            var api = new ApiClient(_http, new ServerOrigin(Server));
            var decryptor = new VaultDecryptor(crypto);
            var cache = new VaultCacheService(new FakeCacheStore(), decryptor, Session);
            var clients = new FakeVaultApiClientProvider()
                .AddAnonymous<IIdentityApiClient>(api)
                .AddAnonymous<IServerConfigApiClient>(api)
                .AddAuthenticated<IReadonlyApiClient>(api)
                .AddAuthenticated<IAccountApiClient>(api);
            Service = new AuthService(
                clients,
                crypto,
                Session,
                Store,
                new VaultBootstrapper(new SyncService(clients, decryptor, Session, cache), clients, Session),
                cache,
                new TokenRefreshService(clients, Session, Store));
        }

        public Task<AuthResult> LoginAsync(
            string clientId = ClientId,
            string masterPassword = Password,
            CancellationToken? ct = null) => Service.LoginWithApiKeyAsync(
                Service.BeginAuthenticationAttempt(),
                Server,
                Email,
                clientId,
                ApiSecret,
                masterPassword,
                ct ?? TestContext.Current.CancellationToken);

        public string TokenJson(
            string tokenAccountId = AccountId,
            string tokenEmail = Email,
            string scope = "api") => JsonSerializer.Serialize(new
            {
                access_token = AccessToken(tokenAccountId, tokenEmail),
                expires_in = 3600,
                token_type = "Bearer",
                scope,
                Key = ProtectedUserKey,
                PrivateKey = (string?)null,
                Kdf = 0,
                KdfIterations = Iterations,
                UserDecryptionOptions = new
                {
                    HasMasterPassword = true,
                    MasterPasswordUnlock = new
                    {
                        Kdf = new { KdfType = 0, Iterations },
                        MasterKeyWrappedUserKey = ProtectedUserKey,
                        Salt = Email,
                    },
                },
            });

        public void EnqueueSuccessfulLogin(string? customTokenJson = null, string? customSyncJson = null)
        {
            Handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, customTokenJson ?? TokenJson()));
            Handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                customSyncJson ?? $$"""{"object":"sync","profile":{"id":"{{AccountId}}","email":"{{Email}}","name":"Me","securityStamp":"stamp-1"},"folders":[],"ciphers":[]}"""));
            Handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"data":[],"object":"list","continuationToken":null}"""));
        }

        public void Dispose()
        {
            Session.Logout();
            UserKey.Dispose();
            _http.Dispose();
        }
    }
}
