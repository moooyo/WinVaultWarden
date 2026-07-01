using System.Security.Cryptography;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class AccountServiceTests
{
    private const string Email = "me@example.com";
    private const string OldPassword = "old-master-password";
    private const string NewPassword = "new-master-password-2!";
    private const int Iterations = 600_000;

    private readonly CryptoService _crypto = new();

    // ---- 辅助：建立已解锁的 session + tokenStore ----

    private (VaultSession session, MemoryTokenStore store, SymmetricCryptoKey userKey, string protectedUserKey)
        BuildUnlockedSession()
    {
        var masterKey = _crypto.DeriveMasterKey(OldPassword, Email, KdfType.Pbkdf2, Iterations, null, null);
        var stretchedKey = _crypto.StretchMasterKey(masterKey);
        CryptographicOperations.ZeroMemory(masterKey);

        var userKey = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var protectedUserKey = _crypto.Encrypt(userKey.FullKey, stretchedKey).ToString();

        var store = new MemoryTokenStore();
        store.Save(new PersistedSession(
            "https://vault.example",
            Email,
            "device-id",
            "refresh",
            protectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null));

        var session = new VaultSession();
        session.SetTokens("access", "refresh");
        session.SetUnlockedKey(userKey);

        return (session, store, userKey, protectedUserKey);
    }

    private AccountService BuildService(
        VaultSession session,
        MemoryTokenStore store,
        FakeAccountApiClient api,
        FakeAuthService auth)
    {
        // FakeAuthService 需要持有 session/store 引用，以便 LogoutAsync 能真实清除它们
        // 通过重新创建确保传入正确引用（构造函数接受 session+store）
        return new AccountService(_crypto, api, session, store, auth);
    }

    // ---- Test 1: ChangePassword 发送重包 key 并登出 ----

    [Fact]
    public async Task ChangePassword_sends_rewrapped_key_then_logs_out()
    {
        var (session, store, userKey, _) = BuildUnlockedSession();
        // 在 session 被 clear（userKey 内存被清零）之前，先保留一份 key 字节副本
        var expectedKeyBytes = userKey.FullKey.ToArray();
        var api = new FakeAccountApiClient();
        var auth = new FakeAuthService(session, store);

        var svc = BuildService(session, store, api, auth);

        await svc.ChangePasswordAsync(OldPassword, NewPassword, "a hint", TestContext.Current.CancellationToken);

        // 断言 API 被调用了且 Key 是 type-2 (AesCbc256_HmacSha256_B64)
        Assert.NotNull(api.Password);
        Assert.StartsWith("2.", api.Password.Key);

        // Hint 透传
        Assert.Equal("a hint", api.Password.MasterPasswordHint);

        // 新 hash 不等于旧 hash
        var oldMk = _crypto.DeriveMasterKey(OldPassword, Email, KdfType.Pbkdf2, Iterations, null, null);
        var oldHash = _crypto.ComputeMasterPasswordHash(oldMk, OldPassword);
        CryptographicOperations.ZeroMemory(oldMk);

        var newMk = _crypto.DeriveMasterKey(NewPassword, Email, KdfType.Pbkdf2, Iterations, null, null);
        var newHash = _crypto.ComputeMasterPasswordHash(newMk, NewPassword);
        CryptographicOperations.ZeroMemory(newMk);

        Assert.Equal(oldHash, api.Password.MasterPasswordHash);
        Assert.Equal(newHash, api.Password.NewMasterPasswordHash);

        // 验证包裹后的 key 解密后等于原始 userKey
        var derivedMk2 = _crypto.DeriveMasterKey(NewPassword, Email, KdfType.Pbkdf2, Iterations, null, null);
        var stretchedNew = _crypto.StretchMasterKey(derivedMk2);
        CryptographicOperations.ZeroMemory(derivedMk2);
        var decrypted = _crypto.DecryptUserKey(stretchedNew, EncString.Parse(api.Password.Key));
        Assert.Equal(expectedKeyBytes, decrypted.FullKey);

        // 改密后强制登出：session.UserKey 已清零 / tokenStore 已清空
        Assert.True(auth.LoggedOut);
        Assert.Null(session.UserKey);
        Assert.False(store.TryLoad(out _));
    }

    // ---- Test 2: ChangePassword 旧密码错误时抛出 AccountOperationException ----

    [Fact]
    public async Task ChangePassword_wrong_current_password_throws_AccountOperationException()
    {
        var (session, store, _, _) = BuildUnlockedSession();
        var api = new FakeAccountApiClient();
        var auth = new FakeAuthService(session, store);
        var svc = BuildService(session, store, api, auth);

        // 传错误的旧密码
        await Assert.ThrowsAsync<AccountOperationException>(() =>
            svc.ChangePasswordAsync("WRONG-PASSWORD", NewPassword, null, TestContext.Current.CancellationToken));

        // 没有发出 API 请求
        Assert.Null(api.Password);
        // 没有登出
        Assert.False(auth.LoggedOut);
    }

    // ---- Test 3: ChangeKdf 低 iterations 客户端拒绝，不发 API ----

    [Fact]
    public async Task ChangeKdf_low_iterations_rejected_clientside()
    {
        var (session, store, _, _) = BuildUnlockedSession();
        var api = new FakeAccountApiClient();
        var auth = new FakeAuthService(session, store);
        var svc = BuildService(session, store, api, auth);

        await Assert.ThrowsAsync<AccountOperationException>(() =>
            svc.ChangeKdfAsync(OldPassword, 99_999, TestContext.Current.CancellationToken));

        Assert.Null(api.Kdf);
        Assert.False(auth.LoggedOut);
    }

    // ---- Test 3b: ChangeKdf 旧密码错误时抛出 AccountOperationException，且不发 API ----

    [Fact]
    public async Task ChangeKdf_wrong_current_password_throws_AccountOperationException()
    {
        var (session, store, _, _) = BuildUnlockedSession();
        var api = new FakeAccountApiClient();
        var auth = new FakeAuthService(session, store);
        var svc = BuildService(session, store, api, auth);

        // 传入错误的旧密码
        await Assert.ThrowsAsync<AccountOperationException>(() =>
            svc.ChangeKdfAsync("WRONG-PASSWORD", 700_000, TestContext.Current.CancellationToken));

        // 没有发出 API 请求
        Assert.Null(api.Kdf);
        // 没有登出
        Assert.False(auth.LoggedOut);
    }

    // ---- Test 4: ChangeKdf 成功时发送正确请求并登出 ----

    [Fact]
    public async Task ChangeKdf_sends_correct_request_and_logs_out()
    {
        var (session, store, userKey, _) = BuildUnlockedSession();
        // 在 session 被 clear（userKey 内存被清零）之前，先保留一份 key 字节副本
        var expectedKeyBytes = userKey.FullKey.ToArray();
        var api = new FakeAccountApiClient();
        var auth = new FakeAuthService(session, store);
        var svc = BuildService(session, store, api, auth);

        const int NewIterations = 700_000;
        await svc.ChangeKdfAsync(OldPassword, NewIterations, TestContext.Current.CancellationToken);

        Assert.NotNull(api.Kdf);

        // kdf params: PBKDF2=0, mem/par=null
        Assert.Equal(0, api.Kdf.AuthenticationData.Kdf.Kdf);
        Assert.Equal(NewIterations, api.Kdf.AuthenticationData.Kdf.KdfIterations);
        Assert.Null(api.Kdf.AuthenticationData.Kdf.KdfMemory);
        Assert.Null(api.Kdf.AuthenticationData.Kdf.KdfParallelism);

        // salt = email
        Assert.Equal(Email, api.Kdf.AuthenticationData.Salt);
        Assert.Equal(Email, api.Kdf.UnlockData.Salt);

        // UnlockData kdf 也用新参数
        Assert.Equal(0, api.Kdf.UnlockData.Kdf.Kdf);
        Assert.Equal(NewIterations, api.Kdf.UnlockData.Kdf.KdfIterations);

        // MasterKeyWrappedUserKey 是 type-2
        Assert.StartsWith("2.", api.Kdf.UnlockData.MasterKeyWrappedUserKey);

        // 验证包裹后的 key 解密后等于原 userKey（使用预先保存的副本）
        var mk = _crypto.DeriveMasterKey(OldPassword, Email, KdfType.Pbkdf2, NewIterations, null, null);
        var stretched = _crypto.StretchMasterKey(mk);
        CryptographicOperations.ZeroMemory(mk);
        var decrypted = _crypto.DecryptUserKey(stretched, EncString.Parse(api.Kdf.UnlockData.MasterKeyWrappedUserKey));
        Assert.Equal(expectedKeyBytes, decrypted.FullKey);

        // 同一密码，新 kdf 派生的 hash 用于 auth+new hash
        var mk2 = _crypto.DeriveMasterKey(OldPassword, Email, KdfType.Pbkdf2, NewIterations, null, null);
        var expectedNewHash = _crypto.ComputeMasterPasswordHash(mk2, OldPassword);
        CryptographicOperations.ZeroMemory(mk2);
        Assert.Equal(expectedNewHash, api.Kdf.NewMasterPasswordHash);
        Assert.Equal(expectedNewHash, api.Kdf.AuthenticationData.MasterPasswordAuthenticationHash);

        // 同样要有旧 hash（masterPasswordHash）= 用旧 kdf 派生
        var oldMk = _crypto.DeriveMasterKey(OldPassword, Email, KdfType.Pbkdf2, Iterations, null, null);
        var expectedOldHash = _crypto.ComputeMasterPasswordHash(oldMk, OldPassword);
        CryptographicOperations.ZeroMemory(oldMk);
        Assert.Equal(expectedOldHash, api.Kdf.MasterPasswordHash);

        // 登出
        Assert.True(auth.LoggedOut);
        Assert.Null(session.UserKey);
        Assert.False(store.TryLoad(out _));
    }

    // ---- Test 5: UpdateName 名字过长时客户端拒绝 ----

    [Fact]
    public async Task UpdateName_too_long_rejected()
    {
        var (session, store, _, _) = BuildUnlockedSession();
        var api = new FakeAccountApiClient();
        var auth = new FakeAuthService(session, store);
        var svc = BuildService(session, store, api, auth);

        var longName = new string('A', 51);
        await Assert.ThrowsAsync<AccountOperationException>(() =>
            svc.UpdateNameAsync(longName, TestContext.Current.CancellationToken));

        Assert.Null(api.Profile);
    }

    // ---- Test 6: UpdateName 成功时发 profile 请求，不登出 ----

    [Fact]
    public async Task UpdateName_sends_profile_update_without_logout()
    {
        var (session, store, _, _) = BuildUnlockedSession();
        var api = new FakeAccountApiClient();
        var auth = new FakeAuthService(session, store);
        var svc = BuildService(session, store, api, auth);

        await svc.UpdateNameAsync("  Alice  ", TestContext.Current.CancellationToken);

        Assert.NotNull(api.Profile);
        Assert.Equal("Alice", api.Profile.Name);  // trimmed
        Assert.Equal("en-US", api.Profile.Culture);
        Assert.False(auth.LoggedOut);
        Assert.NotNull(session.UserKey); // session 保持
    }

    // ---- Test 7: UpdateName 空名字 trim 后也应拒绝 ----

    [Fact]
    public async Task UpdateName_empty_after_trim_rejected()
    {
        var (session, store, _, _) = BuildUnlockedSession();
        var api = new FakeAccountApiClient();
        var auth = new FakeAuthService(session, store);
        var svc = BuildService(session, store, api, auth);

        await Assert.ThrowsAsync<AccountOperationException>(() =>
            svc.UpdateNameAsync("   ", TestContext.Current.CancellationToken));

        Assert.Null(api.Profile);
    }

    // ---- Test 8: VerifyMasterPassword 正确密码返回 true ----

    [Fact]
    public async Task VerifyMasterPassword_correct_returns_true()
    {
        var (session, store, _, _) = BuildUnlockedSession();
        var svc = BuildService(session, store, new FakeAccountApiClient(), new FakeAuthService(session, store));

        Assert.True(await svc.VerifyMasterPasswordAsync(OldPassword, TestContext.Current.CancellationToken));
    }

    // ---- Test 9: VerifyMasterPassword 错误密码返回 false（不抛异常）----

    [Fact]
    public async Task VerifyMasterPassword_wrong_returns_false()
    {
        var (session, store, _, _) = BuildUnlockedSession();
        var svc = BuildService(session, store, new FakeAccountApiClient(), new FakeAuthService(session, store));

        Assert.False(await svc.VerifyMasterPasswordAsync("WRONG-PASSWORD", TestContext.Current.CancellationToken));
    }
}

// ---- FakeAuthService: 追踪 LogoutAsync 调用，并真实清除 session/store ----

public sealed class FakeAuthService : IAuthService
{
    private readonly VaultSession? _session;
    private readonly ITokenStore? _store;

    public bool LoggedOut { get; private set; }

    public FakeAuthService() { }

    public FakeAuthService(VaultSession session, ITokenStore store)
    {
        _session = session;
        _store = store;
    }

    public Task<AuthResult> LoginAsync(string serverUrl, string email, string masterPassword, CancellationToken ct = default)
        => Task.FromResult<AuthResult>(new AuthResult.Failure("not implemented"));

    public Task<AuthResult> SubmitTwoFactorAsync(string code, CancellationToken ct = default, bool rememberDevice = true)
        => Task.FromResult<AuthResult>(new AuthResult.Failure("not implemented"));

    public Task<AuthResult> UnlockAsync(string masterPassword, CancellationToken ct = default)
        => Task.FromResult<AuthResult>(new AuthResult.Failure("not implemented"));

    public Task LockAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task LogoutAsync(CancellationToken ct = default)
    {
        LoggedOut = true;
        _store?.Clear();
        _session?.Clear();
        return Task.CompletedTask;
    }
}
