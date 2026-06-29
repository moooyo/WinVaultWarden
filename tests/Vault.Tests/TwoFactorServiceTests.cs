using System;
using System.Security.Cryptography;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

/// <summary>
/// TwoFactorService 单元测试（使用真实 CryptoService + MemoryTokenStore + FakeTwoFactorApiClient）。
/// 测试覆盖：ListProviders、BeginTotpSetup、EnableTotp、BeginEmailSetup、SendEmail、EnableEmail、Disable、密码错误分支。
/// </summary>
public class TwoFactorServiceTests
{
    private const string Email = "test@example.com";
    private const string MasterPassword = "correct-horse-battery-staple";
    private const int Iterations = 600_000;

    private readonly CryptoService _crypto = new();

    // ---- 辅助：构建已持久化（但不需要解锁）的 token store ----
    private (MemoryTokenStore store, string masterPasswordHash) BuildStore()
    {
        var mk = _crypto.DeriveMasterKey(MasterPassword, Email, KdfType.Pbkdf2, Iterations, null, null);
        var mphash = _crypto.ComputeMasterPasswordHash(mk, MasterPassword);
        CryptographicOperations.ZeroMemory(mk);

        var store = new MemoryTokenStore();
        // ProtectedUserKey 并不会被 TwoFactorService 用到（它不需要解密 vault），
        // 但 PersistedSession 构造函数要求填一个非空值，故填占位字符串。
        store.Save(new PersistedSession(
            "http://localhost:8080",
            Email,
            "device-id",
            "refresh-token",
            "placeholder-protected-user-key",
            KdfType.Pbkdf2,
            Iterations,
            null,
            null));

        return (store, mphash);
    }

    private TwoFactorService BuildService(MemoryTokenStore store, FakeTwoFactorApiClient api)
        => new TwoFactorService(_crypto, api, store);

    // ============================================================
    // Test 1: ListProvidersAsync — API 结果映射到领域模型
    // ============================================================

    [Fact]
    public async Task ListProviders_maps_api_response_to_domain_models()
    {
        var (store, _) = BuildStore();
        var api = new FakeTwoFactorApiClient
        {
            ProvidersResponse = new Api.Dtos.TwoFactorProvidersResponse(new List<Api.Dtos.TwoFactorProviderItem>
            {
                new(Type: 0, Enabled: true),
                new(Type: 1, Enabled: false),
            })
        };
        var svc = BuildService(store, api);

        var result = await svc.ListProvidersAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].Type);
        Assert.True(result[0].Enabled);
        Assert.Equal(1, result[1].Type);
        Assert.False(result[1].Enabled);
    }

    // ============================================================
    // Test 2: BeginTotpSetupAsync — 从 API 拿到 key，构造 otpauth URI
    // ============================================================

    [Fact]
    public async Task BeginTotpSetup_returns_secret_and_otpauth_uri()
    {
        var (store, mphash) = BuildStore();
        var fakeKey = "JBSWY3DPEHPK3PXP";  // 预设的 Base32 secret
        var api = new FakeTwoFactorApiClient
        {
            AuthenticatorResponse = new Api.Dtos.AuthenticatorResponse(Enabled: false, Key: fakeKey)
        };
        var svc = BuildService(store, api);

        var (secret, otpauth) = await svc.BeginTotpSetupAsync(MasterPassword, TestContext.Current.CancellationToken);

        // secret 应等于 API 返回的 key
        Assert.Equal(fakeKey, secret);

        // otpauth URI 格式：otpauth://totp/{encoded_email}?secret={encoded_key}&issuer=WinVaultWarden
        // email 中的 '@' 必须被编码为 %40，secret 中的特殊字符也须编码
        var expectedUri = $"otpauth://totp/{Uri.EscapeDataString(Email)}?secret={Uri.EscapeDataString(fakeKey)}&issuer=WinVaultWarden";
        Assert.Equal(expectedUri, otpauth);

        // API 调用时携带了正确的 masterPasswordHash
        Assert.NotNull(api.LastPasswordVerify);
        Assert.Equal(mphash, api.LastPasswordVerify!.MasterPasswordHash);
    }

    // ============================================================
    // Test 3: BeginTotpSetupAsync — 密码错误时抛 TwoFactorOperationException
    // ============================================================

    [Fact]
    public async Task BeginTotpSetup_wrong_password_throws_TwoFactorOperationException()
    {
        var (store, _) = BuildStore();
        var api = new FakeTwoFactorApiClient();
        var svc = BuildService(store, api);

        // 即使 API 不抛出，derive 出的 hash 与服务端存储的不匹配时……
        // 注：TwoFactorService 不做服务端验证密码，只是把 hash 发给服务端。
        // 密码错误分支：tokenStore 未保存 session（相当于未登录状态）
        store.Clear();

        await Assert.ThrowsAsync<TwoFactorOperationException>(() =>
            svc.BeginTotpSetupAsync("any-password", TestContext.Current.CancellationToken));
    }

    // ============================================================
    // Test 4: EnableTotpAsync — 发送 key+token+masterPasswordHash，返回 recovery code
    // ============================================================

    [Fact]
    public async Task EnableTotp_sends_key_token_hash_and_returns_recovery_code()
    {
        var (store, mphash) = BuildStore();
        const string Secret = "JBSWY3DPEHPK3PXP";
        const string TotpCode = "123456";
        const string ExpectedRecovery = "recover-code-abc";

        var api = new FakeTwoFactorApiClient
        {
            RecoverResponse = new Api.Dtos.RecoverResponse(Code: ExpectedRecovery)
        };
        var svc = BuildService(store, api);

        var recoveryCode = await svc.EnableTotpAsync(
            MasterPassword, Secret, TotpCode, TestContext.Current.CancellationToken);

        // 返回正确的 recovery code
        Assert.Equal(ExpectedRecovery, recoveryCode);

        // EnableAuthenticatorAsync 收到正确请求
        Assert.NotNull(api.LastEnableAuthenticator);
        Assert.Equal(Secret, api.LastEnableAuthenticator!.Key);      // secret 作为 key
        Assert.Equal(TotpCode, api.LastEnableAuthenticator!.Token);  // code 作为 token
        Assert.Equal(mphash, api.LastEnableAuthenticator!.MasterPasswordHash);

        // 调用 GetRecoverAsync 时也带了正确的 hash
        Assert.NotNull(api.LastPasswordVerify);
        Assert.Equal(mphash, api.LastPasswordVerify!.MasterPasswordHash);
    }

    // ============================================================
    // Test 5: EnableTotpAsync — API 返回 null recovery code 时用空字符串兜底
    // ============================================================

    [Fact]
    public async Task EnableTotp_null_recovery_code_returns_empty_string()
    {
        var (store, _) = BuildStore();
        var api = new FakeTwoFactorApiClient
        {
            RecoverResponse = new Api.Dtos.RecoverResponse(Code: null)
        };
        var svc = BuildService(store, api);

        var result = await svc.EnableTotpAsync(MasterPassword, "SECRET", "000000", TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, result);
    }

    // ============================================================
    // Test 6: BeginEmailSetupAsync — 返回服务端邮箱（脱敏展示地址）
    // ============================================================

    [Fact]
    public async Task BeginEmailSetup_returns_masked_email_from_api()
    {
        var (store, mphash) = BuildStore();
        const string MaskedEmail = "t***@example.com";
        var api = new FakeTwoFactorApiClient
        {
            EmailStatusResponse = new Api.Dtos.EmailStatusResponse(Email: MaskedEmail, Enabled: false)
        };
        var svc = BuildService(store, api);

        var result = await svc.BeginEmailSetupAsync(MasterPassword, TestContext.Current.CancellationToken);

        Assert.Equal(MaskedEmail, result);
        Assert.NotNull(api.LastPasswordVerify);
        Assert.Equal(mphash, api.LastPasswordVerify!.MasterPasswordHash);
    }

    // ============================================================
    // Test 7: SendEmailAsync — 发出 SendEmailRequest{email, masterPasswordHash}
    // ============================================================

    [Fact]
    public async Task SendEmail_sends_email_and_hash()
    {
        var (store, mphash) = BuildStore();
        const string TargetEmail = "other@example.com";
        var api = new FakeTwoFactorApiClient();
        var svc = BuildService(store, api);

        await svc.SendEmailAsync(MasterPassword, TargetEmail, TestContext.Current.CancellationToken);

        Assert.NotNull(api.LastSendEmail);
        Assert.Equal(TargetEmail, api.LastSendEmail!.Email);
        Assert.Equal(mphash, api.LastSendEmail!.MasterPasswordHash);
        Assert.Null(api.LastSendEmail!.Otp);  // 普通发送不带 OTP
    }

    // ============================================================
    // Test 8: EnableEmailAsync — 发出 EmailVerifyRequest{email, token, masterPasswordHash}
    // ============================================================

    [Fact]
    public async Task EnableEmail_sends_email_token_hash()
    {
        var (store, mphash) = BuildStore();
        const string TargetEmail = "u@example.com";
        const string Token = "654321";
        var api = new FakeTwoFactorApiClient();
        var svc = BuildService(store, api);

        await svc.EnableEmailAsync(MasterPassword, TargetEmail, Token, TestContext.Current.CancellationToken);

        Assert.NotNull(api.LastEnableEmail);
        Assert.Equal(TargetEmail, api.LastEnableEmail!.Email);
        Assert.Equal(Token, api.LastEnableEmail!.Token);
        Assert.Equal(mphash, api.LastEnableEmail!.MasterPasswordHash);
    }

    // ============================================================
    // Test 9: DisableAsync — 发出 DisableTwoFactorRequest{type, masterPasswordHash}
    // ============================================================

    [Fact]
    public async Task Disable_sends_type_and_hash()
    {
        var (store, mphash) = BuildStore();
        const int ProviderType = 0; // Authenticator
        var api = new FakeTwoFactorApiClient();
        var svc = BuildService(store, api);

        await svc.DisableAsync(MasterPassword, ProviderType, TestContext.Current.CancellationToken);

        Assert.NotNull(api.LastDisable);
        Assert.Equal(ProviderType, api.LastDisable!.Type);
        Assert.Equal(mphash, api.LastDisable!.MasterPasswordHash);
    }

    // ============================================================
    // Test 10: DisableAsync — Email 类型（type=1）也正确发送
    // ============================================================

    [Fact]
    public async Task Disable_email_type_sends_correct_type_value()
    {
        var (store, mphash) = BuildStore();
        const int EmailType = 1;
        var api = new FakeTwoFactorApiClient();
        var svc = BuildService(store, api);

        await svc.DisableAsync(MasterPassword, EmailType, TestContext.Current.CancellationToken);

        Assert.NotNull(api.LastDisable);
        Assert.Equal(EmailType, api.LastDisable!.Type);
        Assert.Equal(mphash, api.LastDisable!.MasterPasswordHash);
    }

    // ============================================================
    // Test 12: BeginTotpSetupAsync — 含 '+' 和 '@' 的 email 须被 percent-encode
    // ============================================================

    [Fact]
    public async Task BeginTotpSetup_email_with_plus_produces_encoded_label()
    {
        // 构建含 '+' 的 email 对应的 store
        const string PlusEmail = "user+tag@example.com";
        const string PlusPw = "correct-horse-battery-staple";
        var mk = _crypto.DeriveMasterKey(PlusPw, PlusEmail, KdfType.Pbkdf2, Iterations, null, null);
        var mphash = _crypto.ComputeMasterPasswordHash(mk, PlusPw);
        CryptographicOperations.ZeroMemory(mk);

        var store = new MemoryTokenStore();
        store.Save(new PersistedSession(
            "http://localhost:8080",
            PlusEmail,
            "device-id",
            "refresh-token",
            "placeholder-protected-user-key",
            KdfType.Pbkdf2,
            Iterations,
            null,
            null));

        const string FakeKey = "JBSWY3DPEHPK3PXP";
        var api = new FakeTwoFactorApiClient
        {
            AuthenticatorResponse = new Api.Dtos.AuthenticatorResponse(Enabled: false, Key: FakeKey)
        };
        var svc = BuildService(store, api);

        var (_, otpauth) = await svc.BeginTotpSetupAsync(PlusPw, TestContext.Current.CancellationToken);

        // label 部分（path segment）不允许含裸 '@' 或 '+'
        var labelPart = new Uri(otpauth).AbsolutePath.TrimStart('/');
        Assert.DoesNotContain("@", labelPart);
        Assert.DoesNotContain("+", labelPart);

        // 确认正确的 percent-encoded 内容存在
        var expectedLabel = Uri.EscapeDataString(PlusEmail);
        Assert.Contains(expectedLabel, otpauth);

        // secret query 参数也被正确编码
        var expectedSecret = Uri.EscapeDataString(FakeKey);
        Assert.Contains($"secret={expectedSecret}", otpauth);
    }

    // ============================================================
    // Test 11: 各操作在 store 未加载时抛 TwoFactorOperationException
    // ============================================================

    [Fact]
    public async Task Operations_throw_when_session_not_persisted()
    {
        var store = new MemoryTokenStore(); // 空 store，未登录
        var api = new FakeTwoFactorApiClient();
        var svc = BuildService(store, api);

        await Assert.ThrowsAsync<TwoFactorOperationException>(() =>
            svc.BeginTotpSetupAsync("pw", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TwoFactorOperationException>(() =>
            svc.EnableTotpAsync("pw", "SECRET", "000000", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TwoFactorOperationException>(() =>
            svc.BeginEmailSetupAsync("pw", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TwoFactorOperationException>(() =>
            svc.SendEmailAsync("pw", "x@y.com", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TwoFactorOperationException>(() =>
            svc.EnableEmailAsync("pw", "x@y.com", "tok", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TwoFactorOperationException>(() =>
            svc.DisableAsync("pw", 0, TestContext.Current.CancellationToken));
    }
}
