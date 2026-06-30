using System.Security.Cryptography;
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
        var svc = new RegisterService(_crypto, api);

        await svc.RegisterAsync(ServerUrl, Email, Name, Password, Hint, TestContext.Current.CancellationToken);

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
        var svc = new RegisterService(_crypto, api);

        await svc.RegisterAsync(ServerUrl, Email, Name, Password, Hint, TestContext.Current.CancellationToken);

        Assert.NotNull(api.Register);
        Assert.Equal(Email, api.Register.Email);
        Assert.Equal(Name, api.Register.Name);
        Assert.Equal(Hint, api.Register.MasterPasswordHint);
    }

    // Test 3: MasterPasswordHash 与 crypto 计算值一致
    [Fact]
    public async Task RegisterAsync_sends_correct_master_password_hash()
    {
        var api = new FakeAccountApiClient();
        var svc = new RegisterService(_crypto, api);

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
        var svc = new RegisterService(_crypto, api);

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
        var svc = new RegisterService(_crypto, api);

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
        var svc = new RegisterService(_crypto, api);

        var ex = await Assert.ThrowsAsync<RegistrationException>(() =>
            svc.RegisterAsync(ServerUrl, Email, Name, Password, Hint, TestContext.Current.CancellationToken));

        Assert.Equal("Registration not allowed", ex.Message);
    }

    // Test 7: SetBaseAddress 传入 serverUrl（去除末尾斜杠）
    [Fact]
    public async Task RegisterAsync_sets_base_address_without_trailing_slash()
    {
        var api = new FakeAccountApiClient();
        var svc = new RegisterService(_crypto, api);

        await svc.RegisterAsync("http://vault.example.com/", Email, Name, Password, Hint,
            TestContext.Current.CancellationToken);

        Assert.Equal("http://vault.example.com", api.LastBaseAddress);
    }
}
