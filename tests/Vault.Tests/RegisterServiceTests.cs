using System.Security.Cryptography;
using System.Net;
using Api;
using Core.Enums;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

// TDD RED: RegisterService 尚不存在，这些测试先写以驱动实现
public class RegisterServiceTests
{
    private const string ServerUrl = "http://vault.example.com";
    private const string Email = "a@b.com";
    private const string Name = "Bob";
    private const string Password = "pw";
    private const string Hint = "hint";

    private readonly CryptoService _crypto = new();

    // 辅助：从注册请求中复现主密钥（用于验证断言）
    private byte[] DeriveMasterKey() =>
        _crypto.DeriveMasterKey(Password, Email, KdfType.Pbkdf2, 600_000, null, null);

    // Test 1: KDF 参数断言 — kdf=0, iter=600000, memory/parallelism=null
    [Fact]
    public async Task RegisterAsync_sends_correct_kdf_params()
    {
        var api = new FakeAccountApiClient();
        var svc = new RegisterService(_crypto, FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        var outcome = await svc.RegisterAsync(
            ServerUrl,
            Email,
            Name,
            Password,
            Hint,
            TestContext.Current.CancellationToken);

        Assert.IsType<RegistrationOutcome.Registered>(outcome);
        Assert.NotNull(api.Register);
        Assert.Equal(0, api.Register.Kdf);
        Assert.Equal(600_000, api.Register.KdfIterations);
        Assert.Null(api.Register.KdfMemory);
        Assert.Null(api.Register.KdfParallelism);
    }

    // Test 2: Email/Name/Hint 正确透传
    [Fact]
    public async Task RegisterAsync_sends_correct_identity_fields()
    {
        var api = new FakeAccountApiClient();
        var svc = new RegisterService(_crypto, FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        await svc.RegisterAsync(ServerUrl, Email, Name, Password, Hint, TestContext.Current.CancellationToken);

        Assert.NotNull(api.Register);
        Assert.Equal(Email, api.Register.Email);
        Assert.Equal(Name, api.Register.Name);
        Assert.Equal(Hint, api.Register.MasterPasswordHint);
    }

    [Fact]
    public async Task RegisterAsync_NormalizesEmailBeforeDerivingAndSendingIdentity()
    {
        const string mixedEmail = " User@Example.COM ";
        const string normalizedEmail = "user@example.com";
        var api = new FakeAccountApiClient();
        var service = new RegisterService(
            _crypto,
            FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        await service.RegisterAsync(
            ServerUrl,
            mixedEmail,
            Name,
            Password,
            Hint,
            TestContext.Current.CancellationToken);

        Assert.NotNull(api.Register);
        Assert.Equal(normalizedEmail, api.Register.Email);
        var masterKey = _crypto.DeriveMasterKey(
            Password,
            normalizedEmail,
            KdfType.Pbkdf2,
            600_000,
            null,
            null);
        try
        {
            Assert.Equal(
                _crypto.ComputeMasterPasswordHash(masterKey, Password),
                api.Register.MasterPasswordHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    // Test 3: MasterPasswordHash 与 crypto 计算值一致
    [Fact]
    public async Task RegisterAsync_sends_correct_master_password_hash()
    {
        var api = new FakeAccountApiClient();
        var svc = new RegisterService(_crypto, FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        await svc.RegisterAsync(ServerUrl, Email, Name, Password, Hint, TestContext.Current.CancellationToken);

        Assert.NotNull(api.Register);

        var masterKey = DeriveMasterKey();
        var expectedHash = _crypto.ComputeMasterPasswordHash(masterKey, Password);
        CryptographicOperations.ZeroMemory(masterKey);

        Assert.Equal(expectedHash, api.Register.MasterPasswordHash);
    }

    // Test 4: Key 能用 stretched master key 解密回 64 字节 UserKey
    [Fact]
    public async Task RegisterAsync_key_decrypts_to_64byte_user_key()
    {
        var api = new FakeAccountApiClient();
        var svc = new RegisterService(_crypto, FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        await svc.RegisterAsync(ServerUrl, Email, Name, Password, Hint, TestContext.Current.CancellationToken);

        Assert.NotNull(api.Register);

        var masterKey = DeriveMasterKey();
        var stretched = _crypto.StretchMasterKey(masterKey);
        CryptographicOperations.ZeroMemory(masterKey);

        var userKey = _crypto.DecryptUserKey(stretched, EncString.Parse(api.Register.Key));
        Assert.Equal(64, userKey.FullKey.Length);
    }

    // Test 5: UserKey 能解密 EncryptedPrivateKey，结果可以作为 PKCS8 导入，
    //         且导出的 SubjectPublicKeyInfo == base64-decoded Keys.PublicKey
    [Fact]
    public async Task RegisterAsync_encrypted_private_key_matches_public_key()
    {
        var api = new FakeAccountApiClient();
        var svc = new RegisterService(_crypto, FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        await svc.RegisterAsync(ServerUrl, Email, Name, Password, Hint, TestContext.Current.CancellationToken);

        Assert.NotNull(api.Register);

        // 解开 UserKey
        var masterKey = DeriveMasterKey();
        var stretched = _crypto.StretchMasterKey(masterKey);
        CryptographicOperations.ZeroMemory(masterKey);
        var userKey = _crypto.DecryptUserKey(stretched, EncString.Parse(api.Register.Key));

        // 用 userKey 解密 EncryptedPrivateKey（RAW UserKey wraps private key, NOT stretched）
        var privateKeyBytes = _crypto.Decrypt(EncString.Parse(api.Register.Keys.EncryptedPrivateKey), userKey);

        // 导入为 PKCS8 私钥
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(privateKeyBytes, out _);

        // 导出公钥，比对 Keys.PublicKey（base64 SubjectPublicKeyInfo）
        var exportedPublicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        Assert.Equal(api.Register.Keys.PublicKey, exportedPublicKey);
    }

    // Test 6: VaultWriteException 冒泡为 RegistrationException
    [Fact]
    public async Task RegisterAsync_server_error_throws_RegistrationException()
    {
        var api = new FakeAccountApiClient { Throw = new VaultWriteException("Registration not allowed") };
        var svc = new RegisterService(_crypto, FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        var ex = await Assert.ThrowsAsync<RegistrationException>(() =>
            svc.RegisterAsync(ServerUrl, Email, Name, Password, Hint, TestContext.Current.CancellationToken));

        Assert.Equal("Registration not allowed", ex.Message);
    }

    [Fact]
    public async Task RegisterAsync_OfficialFlow_RequestsTokenThenFinishes()
    {
        var api = new FakeAccountApiClient { SupportsRegistrationFinish = true };
        var svc = new RegisterService(
            _crypto,
            FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        var outcome = await svc.RegisterAsync(
            ServerUrl,
            Email,
            Name,
            Password,
            Hint,
            TestContext.Current.CancellationToken);

        Assert.IsType<RegistrationOutcome.Registered>(outcome);
        Assert.NotNull(api.RegisterVerification);
        Assert.Equal(Email, api.RegisterVerification.Email);
        Assert.Equal(Name, api.RegisterVerification.Name);
        Assert.False(api.RegisterVerification.ReceiveMarketingEmails);
        Assert.NotNull(api.RegisterFinish);
        var token = Assert.IsType<RegistrationVerificationResult.InlineToken>(
            api.RegistrationVerificationResult);
        Assert.Equal(token.Token, api.RegisterFinish.EmailVerificationToken);
        Assert.Equal(Email, api.RegisterFinish.Email);
        Assert.Equal(Hint, api.RegisterFinish.MasterPasswordHint);
        Assert.Equal(600_000, api.RegisterFinish.KdfIterations);
        Assert.Null(api.Register);
    }

    [Fact]
    public async Task RegisterAsync_OfficialNoContent_ReturnsEmailVerificationRequiredWithoutFinish()
    {
        var api = new FakeAccountApiClient
        {
            SupportsRegistrationFinish = true,
            RegistrationVerificationResult =
                new RegistrationVerificationResult.EmailVerificationSent(),
        };
        var svc = new RegisterService(
            _crypto,
            FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        var outcome = await svc.RegisterAsync(
            ServerUrl,
            Email,
            Name,
            Password,
            Hint,
            TestContext.Current.CancellationToken);

        Assert.IsType<RegistrationOutcome.EmailVerificationRequired>(outcome);
        Assert.Null(api.RegisterFinish);
        Assert.Null(api.Register);
        Assert.Equal(1, api.RegistrationVerificationRequestCount);
        Assert.Equal(0, api.RegistrationFinishRequestCount);
        Assert.Equal(0, api.LegacyRegistrationRequestCount);
    }

    [Fact]
    public async Task RegisterAsync_NonNotFoundVerificationFailure_DoesNotDowngradeToLegacy()
    {
        var api = new FakeAccountApiClient
        {
            Throw = new VaultWriteException(
                HttpStatusCode.BadRequest,
                "Registration blocked",
                null),
        };
        var svc = new RegisterService(
            _crypto,
            FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        var error = await Assert.ThrowsAsync<RegistrationException>(() =>
            svc.RegisterAsync(
                ServerUrl,
                Email,
                Name,
                Password,
                Hint,
                TestContext.Current.CancellationToken));

        Assert.Equal("Registration blocked", error.Message);
        Assert.Null(api.Register);
        Assert.Equal(0, api.LegacyRegistrationRequestCount);
    }

    [Fact]
    public async Task RegisterAsync_OnlyVerificationNotFoundFallsBackToLegacyRegistration()
    {
        var api = new FakeAccountApiClient
        {
            SupportsRegistrationFinish = false,
        };
        var service = new RegisterService(
            _crypto,
            FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        var outcome = await service.RegisterAsync(
            ServerUrl,
            Email,
            Name,
            Password,
            Hint,
            TestContext.Current.CancellationToken);

        Assert.IsType<RegistrationOutcome.Registered>(outcome);
        Assert.NotNull(api.Register);
        Assert.Null(api.RegisterFinish);
        Assert.Equal(1, api.RegistrationVerificationRequestCount);
        Assert.Equal(1, api.LegacyRegistrationRequestCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RegisterAsync_StateChangingTimeout_ReturnsOutcomeUncertain(
        bool officialFlow)
    {
        var timeout = new TaskCanceledException(
            "request timed out",
            new RequestTimeoutException(
                RequestTimeoutPhase.ControlPlane,
                TimeSpan.FromSeconds(30)),
            CancellationToken.None);
        var api = new FakeAccountApiClient
        {
            SupportsRegistrationFinish = officialFlow,
            RegistrationFinishThrow = officialFlow ? timeout : null,
            LegacyRegistrationThrow = officialFlow ? null : timeout,
        };
        var service = new RegisterService(
            _crypto,
            FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        var outcome = await service.RegisterAsync(
            ServerUrl,
            Email,
            Name,
            Password,
            Hint,
            TestContext.Current.CancellationToken);

        Assert.IsType<RegistrationOutcome.OutcomeUncertain>(outcome);
        Assert.Equal(officialFlow ? 1 : 0, api.RegistrationFinishRequestCount);
        Assert.Equal(officialFlow ? 0 : 1, api.LegacyRegistrationRequestCount);
    }

    [Fact]
    public async Task RegisterAsync_VerificationTimeout_IsNotConvertedToPendingOrUncertain()
    {
        var timeout = new TaskCanceledException(
            "request timed out",
            new RequestTimeoutException(
                RequestTimeoutPhase.ControlPlane,
                TimeSpan.FromSeconds(30)),
            CancellationToken.None);
        var api = new FakeAccountApiClient
        {
            SupportsRegistrationFinish = true,
            RegistrationVerificationThrow = timeout,
        };
        var service = new RegisterService(
            _crypto,
            FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            service.RegisterAsync(
                ServerUrl,
                Email,
                Name,
                Password,
                Hint,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, api.RegistrationFinishRequestCount);
        Assert.Equal(0, api.LegacyRegistrationRequestCount);
    }

    [Fact]
    public async Task RegisterAsync_VerificationTransportFailure_IsRecoverableRegistrationError()
    {
        var api = new FakeAccountApiClient
        {
            SupportsRegistrationFinish = true,
            RegistrationVerificationThrow = new HttpRequestException("network unavailable"),
        };
        var service = new RegisterService(
            _crypto,
            FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        var error = await Assert.ThrowsAsync<RegistrationException>(() =>
            service.RegisterAsync(
                ServerUrl,
                Email,
                Name,
                Password,
                Hint,
                TestContext.Current.CancellationToken));

        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal(0, api.RegistrationFinishRequestCount);
        Assert.Equal(0, api.LegacyRegistrationRequestCount);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.RequestTimeout)]
    [InlineData(true, HttpStatusCode.InternalServerError)]
    [InlineData(false, HttpStatusCode.BadGateway)]
    public async Task RegisterAsync_StateChangingAmbiguousHttpFailure_ReturnsOutcomeUncertain(
        bool officialFlow,
        HttpStatusCode statusCode)
    {
        var failure = new VaultWriteException(statusCode, "ambiguous write response", null);
        var api = new FakeAccountApiClient
        {
            SupportsRegistrationFinish = officialFlow,
            RegistrationFinishThrow = officialFlow ? failure : null,
            LegacyRegistrationThrow = officialFlow ? null : failure,
        };
        var service = new RegisterService(
            _crypto,
            FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        var outcome = await service.RegisterAsync(
            ServerUrl,
            Email,
            Name,
            Password,
            Hint,
            TestContext.Current.CancellationToken);

        Assert.IsType<RegistrationOutcome.OutcomeUncertain>(outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RegisterAsync_UnexpectedSuccessfulWriteStatus_ReturnsOutcomeUncertain(
        bool officialFlow)
    {
        var api = new FakeAccountApiClient
        {
            SupportsRegistrationFinish = officialFlow,
            RegistrationFinishResult = new RegistrationWriteResult.OutcomeUncertain(),
            LegacyRegistrationResult = new RegistrationWriteResult.OutcomeUncertain(),
        };
        var service = new RegisterService(
            _crypto,
            FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        var outcome = await service.RegisterAsync(
            ServerUrl,
            Email,
            Name,
            Password,
            Hint,
            TestContext.Current.CancellationToken);

        Assert.IsType<RegistrationOutcome.OutcomeUncertain>(outcome);
    }

    [Fact]
    public async Task RegisterAsync_CancelledBeforeStateChangingRequest_DoesNotBecomeUncertain()
    {
        var api = new FakeAccountApiClient { SupportsRegistrationFinish = true };
        var service = new RegisterService(
            _crypto,
            FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.RegisterAsync(
                ServerUrl,
                Email,
                Name,
                Password,
                Hint,
                cancellation.Token));

        Assert.Equal(0, api.RegistrationFinishRequestCount);
        Assert.Equal(0, api.LegacyRegistrationRequestCount);
    }

    [Theory]
    [InlineData("https://user@vault.example")]
    [InlineData("https://vault.example/path?query=1")]
    [InlineData("https://vault.example/path#fragment")]
    public async Task RegisterAsync_UnsafeServerUrl_IsRecoverableValidationError(string serverUrl)
    {
        var api = new FakeAccountApiClient { SupportsRegistrationFinish = true };
        var service = new RegisterService(
            _crypto,
            FakeVaultApiClientProvider.ForAnonymous<IRegistrationApiClient>(api));

        await Assert.ThrowsAsync<RegistrationException>(() =>
            service.RegisterAsync(
                serverUrl,
                Email,
                Name,
                Password,
                Hint,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, api.RegistrationVerificationRequestCount);
    }
}
