using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

/// <summary>
/// 账户管理编排层：UpdateName / ChangePassword / ChangeKdf。
/// 改密和改 KDF 成功后强制 LogoutAsync（会话清空，用户需重新登录）。
/// 需要 vault 已解锁（session.UserKey != null）。
/// </summary>
public sealed class AccountService : IAccountService
{
    private const int MinIterations = 100_000;
    private const int MaxNameLength = 50;

    private readonly CryptoService _crypto;
    private readonly IAccountApiClient _api;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;
    private readonly IAuthService _auth;

    public AccountService(
        CryptoService crypto,
        IAccountApiClient api,
        VaultSession session,
        ITokenStore tokenStore,
        IAuthService auth)
    {
        _crypto = crypto;
        _api = api;
        _session = session;
        _tokenStore = tokenStore;
        _auth = auth;
    }

    // -----------------------------------------------------------------------
    // UpdateNameAsync: Trim → 长度校验 → POST /api/accounts/profile → 不登出
    // -----------------------------------------------------------------------

    public async Task UpdateNameAsync(string name, CancellationToken ct = default)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            throw new AccountOperationException("名字不能为空。");

        if (trimmed.Length > MaxNameLength)
            throw new AccountOperationException($"名字不能超过 {MaxNameLength} 个字符（当前 {trimmed.Length} 个）。");

        await _api.UpdateProfileAsync(new ProfileUpdateRequest(trimmed, "en-US"), ct);
    }

    // -----------------------------------------------------------------------
    // ChangePasswordAsync:
    //   1. 验证旧密码（尝试用旧密码重新派生 masterKey，然后尝试解密 UserKey）
    //   2. 用新密码派生新 masterKey → ProtectUserKey → newMasterPasswordHash
    //   3. POST /api/accounts/password
    //   4. LogoutAsync（强制重新登录）
    // -----------------------------------------------------------------------

    public async Task ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        string? hint,
        CancellationToken ct = default)
    {
        RequireUnlocked();

        if (!_tokenStore.TryLoad(out var persisted))
            throw new AccountOperationException("无法加载持久化会话，请重新登录后重试。");

        // 验证旧密码：派生旧 masterKey 后尝试解密 UserKey，不匹配则抛异常
        byte[] oldMasterKey = _crypto.DeriveMasterKey(
            currentPassword,
            persisted.Email,
            persisted.KdfType,
            persisted.KdfIterations,
            persisted.KdfMemory,
            persisted.KdfParallelism);

        try
        {
            ValidateCurrentPassword(oldMasterKey, persisted.ProtectedUserKey);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            CryptographicOperations.ZeroMemory(oldMasterKey);
            throw new AccountOperationException("当前密码不正确。");
        }

        string oldHash;
        try
        {
            oldHash = _crypto.ComputeMasterPasswordHash(oldMasterKey, currentPassword);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldMasterKey);
        }

        // 用新密码 + 原 KDF 参数派生新 masterKey
        byte[] newMasterKey = _crypto.DeriveMasterKey(
            newPassword,
            persisted.Email,
            persisted.KdfType,
            persisted.KdfIterations,
            persisted.KdfMemory,
            persisted.KdfParallelism);

        string newHash;
        string wrappedKey;
        try
        {
            newHash = _crypto.ComputeMasterPasswordHash(newMasterKey, newPassword);
            var protectedKey = _crypto.ProtectUserKey(newMasterKey, _session.UserKey!);
            wrappedKey = protectedKey.ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newMasterKey);
        }

        await _api.ChangePasswordAsync(new ChangePasswordRequest(
            MasterPasswordHash: oldHash,
            NewMasterPasswordHash: newHash,
            MasterPasswordHint: hint,
            Key: wrappedKey), ct);

        await _auth.LogoutAsync(ct);
    }

    // -----------------------------------------------------------------------
    // ChangeKdfAsync:
    //   1. 客户端校验 newIterations >= 100_000
    //   2. 验证旧密码（同 ChangePassword）
    //   3. 用旧密码 + 新 KDF 参数重新派生 masterKey → 新 hash + 新 wrappedKey
    //   4. 构建 ChangeKdfRequest（kdf=0, mem/par=null, salt=email）
    //   5. POST /api/accounts/kdf → LogoutAsync
    // -----------------------------------------------------------------------

    public async Task ChangeKdfAsync(
        string currentPassword,
        int newIterations,
        CancellationToken ct = default)
    {
        if (newIterations < MinIterations)
            throw new AccountOperationException(
                $"迭代次数不能低于 {MinIterations}（当前 {newIterations}）。");

        RequireUnlocked();

        if (!_tokenStore.TryLoad(out var persisted))
            throw new AccountOperationException("无法加载持久化会话，请重新登录后重试。");

        // 验证旧密码
        byte[] oldMasterKey = _crypto.DeriveMasterKey(
            currentPassword,
            persisted.Email,
            persisted.KdfType,
            persisted.KdfIterations,
            persisted.KdfMemory,
            persisted.KdfParallelism);

        try
        {
            ValidateCurrentPassword(oldMasterKey, persisted.ProtectedUserKey);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            CryptographicOperations.ZeroMemory(oldMasterKey);
            throw new AccountOperationException("当前密码不正确。");
        }

        string oldHash;
        try
        {
            oldHash = _crypto.ComputeMasterPasswordHash(oldMasterKey, currentPassword);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldMasterKey);
        }

        // 用旧密码 + 新 KDF 参数派生新 masterKey
        // 注意：ChangeKdf 使用 PBKDF2，memory/parallelism 均为 null
        const int newKdf = 0; // KdfType.Pbkdf2
        byte[] newMasterKey = _crypto.DeriveMasterKey(
            currentPassword,
            persisted.Email,
            KdfType.Pbkdf2,
            newIterations,
            null,
            null);

        string newHash;
        string wrappedKey;
        try
        {
            newHash = _crypto.ComputeMasterPasswordHash(newMasterKey, currentPassword);
            var protectedKey = _crypto.ProtectUserKey(newMasterKey, _session.UserKey!);
            wrappedKey = protectedKey.ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(newMasterKey);
        }

        var kdfParams = new KdfParams(
            Kdf: newKdf,
            KdfIterations: newIterations,
            KdfMemory: null,
            KdfParallelism: null);

        var request = new ChangeKdfRequest(
            NewMasterPasswordHash: newHash,
            Key: wrappedKey,
            AuthenticationData: new AuthData(
                Salt: persisted.Email,
                Kdf: kdfParams,
                MasterPasswordAuthenticationHash: newHash),
            UnlockData: new UnlockData(
                Salt: persisted.Email,
                Kdf: kdfParams,
                MasterKeyWrappedUserKey: wrappedKey),
            MasterPasswordHash: oldHash);

        await _api.ChangeKdfAsync(request, ct);
        await _auth.LogoutAsync(ct);
    }

    // -----------------------------------------------------------------------
    // 私有辅助
    // -----------------------------------------------------------------------

    private void RequireUnlocked()
    {
        if (_session.UserKey is null)
            throw new AccountOperationException("操作需要已解锁的 Vault，请先解锁后重试。");
    }

    /// <summary>
    /// 尝试用 masterKey 解密 protectedUserKey。如果解密失败会抛出 CryptographicException，
    /// 调用者捕获后将其转换为 AccountOperationException("当前密码不正确")。
    /// </summary>
    private void ValidateCurrentPassword(byte[] masterKey, string protectedUserKey)
    {
        var stretchedKey = _crypto.StretchMasterKey(masterKey);
        var encString = EncString.Parse(protectedUserKey);
        _crypto.DecryptUserKey(stretchedKey, encString); // throws CryptographicException if wrong
    }
}
