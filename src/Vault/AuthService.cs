using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;

namespace Vault;

public sealed class AuthService : IAuthService
{
    private const string DeviceName = "WinVaultWarden";
    // Bitwarden TwoFactorProviderType.Email。启用邮箱 2FA 的账户在此值出现于 providers 时需登录期发码。
    private const int EmailTwoFactorProvider = 1;
    // Bitwarden TwoFactorProviderType.Remember。二次登录带此 provider + 记住令牌可免交互 2FA。
    private const int RememberTwoFactorProvider = 5;

    private readonly IVaultApiClientProvider _clients;
    private readonly CryptoService _crypto;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;
    private readonly VaultBootstrapper _bootstrapper;
    private readonly VaultCacheService _cache;
    private readonly ITokenRefresher _refresher;
    private PendingTwoFactorContext? _pendingTwoFactor;

    public AuthService(
        IVaultApiClientProvider clients,
        CryptoService crypto,
        VaultSession session,
        ITokenStore tokenStore,
        VaultBootstrapper bootstrapper,
        VaultCacheService cache,
        ITokenRefresher refresher)
    {
        _clients = clients;
        _crypto = crypto;
        _session = session;
        _tokenStore = tokenStore;
        _bootstrapper = bootstrapper;
        _cache = cache;
        _refresher = refresher;
    }

    // 匿名 identity 角色按 origin 获取;登录/刷新/发码全部走匿名 transport,绝不携带 Bearer。
    private IIdentityApiClient Identity(ServerOrigin origin) =>
        _clients.GetAnonymous<IIdentityApiClient>(origin);

    public async Task<AuthResult> LoginAsync(
        string serverUrl,
        string email,
        string masterPassword,
        CancellationToken ct = default)
    {
        try
        {
            var origin = new ServerOrigin(NormalizeServerUrl(serverUrl));
            var api = Identity(origin);

            // prelogin/token 返回前不得改动当前(可能属于账户 A 的)会话状态,也不得开启 B 的 lease。
            var prelogin = await api.PreloginAsync(email, ct);

            // 同一账户二次登录:复用上次的 device identifier(服务端把"记住此设备"令牌按 device 存储,
            // 换新 id 会令令牌失效),并带上已存的 remember 令牌免交互 2FA。仅在同 origin + 同 email 下复用。
            var deviceIdentifier = NewDeviceIdentifier();
            TwoFactorPayload? rememberPayload = null;
            if (_tokenStore.TryLoad(out var existing)
                && existing.Origin == origin
                && string.Equals(existing.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                deviceIdentifier = existing.DeviceIdentifier;
                if (!string.IsNullOrEmpty(existing.TwoFactorToken))
                    rememberPayload = new TwoFactorPayload(RememberTwoFactorProvider, existing.TwoFactorToken, false);
            }

            var context = BuildLoginContext(origin, email, masterPassword, prelogin, deviceIdentifier);
            var result = await api.ConnectTokenAsync(ConnectTokenRequest.Password(
                email,
                context.PasswordHash,
                context.DeviceIdentifier,
                DeviceName,
                rememberPayload), ct);

            return await HandleLoginResultAsync(context, result, ct, autoSendEmail: true);
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            return new AuthResult.Failure(ex.Message);
        }
    }

    public async Task<AuthResult> SubmitTwoFactorAsync(string code, int provider, CancellationToken ct = default, bool rememberDevice = true)
    {
        if (_pendingTwoFactor is not { } context)
            return new AuthResult.Failure("No two-factor challenge is pending.");

        try
        {
            var result = await Identity(context.Origin).ConnectTokenAsync(ConnectTokenRequest.Password(
                context.Email,
                context.PasswordHash,
                context.DeviceIdentifier,
                DeviceName,
                new TwoFactorPayload(provider, code, rememberDevice)), ct);

            return await HandleLoginResultAsync(context with { Providers = Array.Empty<int>() }, result, ct);
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            return new AuthResult.Failure(ex.Message);
        }
    }

    public async Task<AuthResult> UnlockAsync(string masterPassword, CancellationToken ct = default)
    {
        if (!_tokenStore.TryLoad(out var persisted) || string.IsNullOrEmpty(persisted.ProtectedUserKey))
            return new AuthResult.Failure("No saved session.");

        SymmetricCryptoKey userKey;
        try
        {
            var stretchedKey = DeriveStretchedKey(
                masterPassword,
                persisted.Email,
                persisted.KdfType,
                persisted.KdfIterations,
                persisted.KdfMemory,
                persisted.KdfParallelism);
            try
            {
                userKey = _crypto.DecryptUserKey(stretchedKey, EncString.Parse(persisted.ProtectedUserKey));
            }
            finally
            {
                // 拉伸子钥仅用于解包 UserKey,用完清零;存入 session 的 userKey 是独立数组,不受影响。
                stretchedKey.Dispose();
            }
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            _session.Lock();
            return new AuthResult.Failure(ex.Message);
        }

        return await FinishLocalUnlockAsync(persisted, userKey, ct);
    }

    public async Task<AuthResult> UnlockWithPinAsync(string pin, CancellationToken ct = default)
    {
        if (!_tokenStore.TryLoad(out var persisted) || string.IsNullOrEmpty(persisted.PinProtectedUserKey))
            return new AuthResult.Failure("未设置 PIN。");

        SymmetricCryptoKey userKey;
        try
        {
            var stretchedKey = DeriveStretchedKey(
                pin, persisted.Email, persisted.KdfType,
                persisted.KdfIterations, persisted.KdfMemory, persisted.KdfParallelism);
            try
            {
                userKey = _crypto.DecryptUserKey(stretchedKey, EncString.Parse(persisted.PinProtectedUserKey));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return HandleWrongPin(persisted);
            }
            finally
            {
                // 拉伸子钥用完清零;存入 session 的 userKey 是独立数组,不受影响。
                stretchedKey.Dispose();
            }
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            _session.Lock();
            return new AuthResult.Failure(ex.Message);
        }

        // 成功解包 → 清零失败计数,经 normalized 记录在解锁门禁内落盘。
        return await FinishLocalUnlockAsync(persisted with { PinFailedAttempts = 0 }, userKey, ct);
    }

    // PIN 错误:经恢复出的 locked lease 门禁写回失败计数/清 PIN,绝不在 TryCommit 外触碰 token store。
    private AuthResult HandleWrongPin(PersistedSession persisted)
    {
        var lease = _session.RestorePersistedSession(persisted);
        var n = persisted.PinFailedAttempts + 1;
        if (n >= 5)
        {
            _session.TryCommit(lease, () => _tokenStore.Save(persisted with
            {
                PinProtectedUserKey = null,
                PinFailedAttempts = 0,
                Generation = lease.Generation,
            }));
            return new AuthResult.PinCleared("PIN 已因多次错误被清除，请用主密码解锁。");
        }
        _session.TryCommit(lease, () => _tokenStore.Save(persisted with
        {
            PinFailedAttempts = n,
            Generation = lease.Generation,
        }));
        return new AuthResult.Failure($"PIN 错误，还可尝试 {5 - n} 次。");
    }

    public async Task<AuthResult> UnlockWithHelloAsync(byte[] signature, CancellationToken ct = default)
    {
        if (!_tokenStore.TryLoad(out var persisted) || string.IsNullOrEmpty(persisted.HelloProtectedUserKey))
        {
            CryptographicOperations.ZeroMemory(signature);
            return new AuthResult.Failure("未启用 Windows Hello。");
        }

        SymmetricCryptoKey userKey;
        try
        {
            using var helloKey = _crypto.DeriveKeyFromSecret(signature);
            try
            {
                userKey = _crypto.DecryptUserKey(helloKey, EncString.Parse(persisted.HelloProtectedUserKey));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Hello 凭据失效(重录/移除)或数据损坏 → 经恢复出的 locked lease 门禁清除 Hello,回退主密码。
                var lease = _session.RestorePersistedSession(persisted);
                _session.TryCommit(lease, () => _tokenStore.Save(persisted with
                {
                    HelloProtectedUserKey = null,
                    HelloChallenge = null,
                    Generation = lease.Generation,
                }));
                return new AuthResult.Failure("Windows Hello 解锁失败，请用主密码解锁。");
            }
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            _session.Lock();
            return new AuthResult.Failure(ex.Message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }

        return await FinishLocalUnlockAsync(persisted, userKey, ct);
    }

    // 主密码/PIN/Hello 三入口共用:恢复绑定 → 门禁落 UserKey + 归一化持久化 → 缓存即时出数据 →
    // 统一走 refresher/bootstrap;网络失败按有无缓存分流。禁止在此复制三套刷新/缓存/引导逻辑。
    private async Task<AuthResult> FinishLocalUnlockAsync(
        PersistedSession persisted,
        SymmetricCryptoKey userKey,
        CancellationToken ct)
    {
        var lease = _session.RestorePersistedSession(persisted);
        var normalized = persisted with { Generation = lease.Generation };

        var transferred = false;
        try
        {
            if (!_session.TryCommit(lease, () =>
            {
                // 归一化持久化(消费 Task 6 generation 轮换),同一门禁内落 UserKey 与回种 refresh token。
                _tokenStore.Save(normalized);
                _session.SetUnlockedKey(userKey);
                _session.SetRefreshTokenOnly(normalized.RefreshToken);
                transferred = true;
            }))
                return new AuthResult.Failure("Unlock session expired.");
        }
        finally
        {
            if (!transferred)
                userKey.Dispose();
        }

        // generation 轮换后(同会话内 Lock/Unlock)让旧缓存重新对齐当前租约,供 TryLoadInto 命中。
        if (persisted.Generation != lease.Generation)
            _cache.RebindGeneration(lease);

        if (!_session.TryCopyUserKey(lease, out var cacheKey))
            return new AuthResult.Failure("Unlock session expired.");
        bool fromCache;
        using (cacheKey)
            fromCache = _cache.TryLoadInto(lease, cacheKey!);

        try
        {
            if (!await _refresher.TryRefreshAsync(lease, ct).ConfigureAwait(false))
                return FinishUnlockOffline(lease, fromCache);

            await _bootstrapper.BootstrapAsync(lease, ct).ConfigureAwait(false);
            return new AuthResult.Success();
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            // 刷新成功但整库同步失败(如账户错配 InvalidDataException/网络抖动)→ 按有无缓存分流。
            return FinishUnlockOffline(lease, fromCache);
        }
    }

    // 网络刷新/整库同步失败后的收尾:有缓存 → 保持已解锁状态(DataStale 已由 TryLoadInto 置 true),
    // 离线解锁成功;无缓存 → 回滚(Lock 清零 UserKey)并失败。两条分支都在租约门禁内提交。
    private AuthResult FinishUnlockOffline(VaultSessionLease lease, bool fromCache)
    {
        if (fromCache
            && _session.TryCommit(lease, () => _session.SetState(VaultState.Unlocked)))
            return new AuthResult.Success();

        _session.TryCommit(lease, () => _session.Lock());
        return new AuthResult.Failure("无法连接服务器,且无离线缓存,请联网后重试。");
    }

    // core 原语:第一动作即失效旧 lease(rotation-first);不停止通知、不清剪贴板、不导航——
    // 第三层 App 生命周期协调器必须在外层阻止新敏感操作并完成 App 编排。
    public Task LockAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var lockedLease = _session.Lock();
        if (lockedLease is not null)
        {
            _session.TryCommit(lockedLease, () =>
            {
                if (_tokenStore.TryLoad(out var persisted)
                    && persisted.AccountId == lockedLease.AccountId
                    && persisted.Origin == lockedLease.Origin)
                    _tokenStore.Save(persisted with
                    {
                        Generation = lockedLease.Generation,
                    });
                _cache.RebindGeneration(lockedLease);
            });
        }
        DisposePendingTwoFactor();
        return Task.CompletedTask;
    }

    public Task LogoutAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _session.Logout();
        _tokenStore.Clear();
        _cache.Clear();
        DisposePendingTwoFactor();
        return Task.CompletedTask;
    }

    // 放弃/登出时清零并丢弃挂起 2FA 上下文里的拉伸主密钥。
    private void DisposePendingTwoFactor()
    {
        _pendingTwoFactor?.StretchedKey.Dispose();
        _pendingTwoFactor = null;
    }

    private async Task<AuthResult> HandleLoginResultAsync(
        PendingTwoFactorContext context,
        ConnectTokenResult result,
        CancellationToken ct,
        bool autoSendEmail = false)
    {
        switch (result)
        {
            case ConnectTokenResult.Success success:
                _pendingTwoFactor = null;
                return await CompleteLoginAsync(context, success.Token, ct);
            case ConnectTokenResult.TwoFactorRequired twoFactor:
                // 若已存在一个过期的 pending 2FA(其拉伸密钥与本次不是同一对象),先清零旧的再替换,防止泄漏。
                // 2FA 重试时 context.StretchedKey 与旧 _pendingTwoFactor 是同一对象,ReferenceEquals 守卫避免误清。
                if (_pendingTwoFactor is { } stale && !ReferenceEquals(stale.StretchedKey, context.StretchedKey))
                    stale.StretchedKey.Dispose();
                var pending = context with { Providers = twoFactor.Providers.ToArray() };
                _pendingTwoFactor = pending;
                // 仅在首次进入验证码阶段(LoginAsync 路径)自动发码;2FA 提交失败再次挑战时不重复发信(避免每次错码都发一封)。
                if (autoSendEmail && twoFactor.Providers.Contains(EmailTwoFactorProvider))
                    await TrySendTwoFactorEmailAsync(pending, ct);
                return new AuthResult.TwoFactorRequired(twoFactor.Providers);
            default:
                return ToFailure(result);
        }
    }

    // 登录期发码:best-effort。失败(服务端未启用邮箱2FA / 网络抖动)不阻断进入验证码阶段,用户可点"重发"重试。
    private async Task TrySendTwoFactorEmailAsync(PendingTwoFactorContext context, CancellationToken ct)
    {
        try
        {
            await Identity(context.Origin).SendTwoFactorEmailLoginAsync(context.Email, context.PasswordHash, context.DeviceIdentifier, ct);
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
        }
    }

    public async Task<bool> ResendTwoFactorEmailAsync(CancellationToken ct = default)
    {
        if (_pendingTwoFactor is not { } context)
            return false;

        try
        {
            await Identity(context.Origin).SendTwoFactorEmailLoginAsync(context.Email, context.PasswordHash, context.DeviceIdentifier, ct);
            return true;
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            return false;
        }
    }

    private async Task<AuthResult> CompleteLoginAsync(
        PendingTwoFactorContext context,
        TokenResponse token,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token.Key))
                return new AuthResult.Failure("Token response did not include a protected user key.");
            // fail-closed:AccountId 只从 access token JWT 的 sub 派生,缺失/非法一律拒绝建立会话。
            if (!AccessTokenSubjectReader.TryReadAccountId(token.AccessToken, out var accountId))
                return new AuthResult.Failure("Token response did not include a valid account subject.");

            var userKey = _crypto.DecryptUserKey(context.StretchedKey, EncString.Parse(token.Key));
            var lease = _session.BeginAccountSession(accountId, context.Email, context.Origin);
            var persisted = new PersistedSession(
                PersistedSession.CurrentSchemaVersion,
                accountId,
                context.Origin,
                lease.Generation,
                context.Email,
                context.DeviceIdentifier,
                token.RefreshToken,
                token.Key,
                context.KdfType,
                context.KdfIterations,
                context.KdfMemory,
                context.KdfParallelism)
            {
                TwoFactorToken = ResolveRememberToken(context, token),
            };

            var transferred = false;
            try
            {
                if (!_session.TryCommit(lease, () =>
                {
                    _tokenStore.Save(persisted);
                    _session.SetTokens(token.AccessToken, token.RefreshToken);
                    _session.SetUnlockedKey(userKey);
                    _session.SetEncryptedPrivateKey(token.PrivateKey);
                    transferred = true;
                }))
                    return new AuthResult.Failure("Login session expired.");
            }
            finally
            {
                if (!transferred)
                    userKey.Dispose();
            }

            try
            {
                await _bootstrapper.BootstrapAsync(lease, ct).ConfigureAwait(false);
                return new AuthResult.Success();
            }
            catch (Exception ex) when (
                ex is OperationCanceledException
                    or HttpRequestException
                    or VaultApiException
                    or CryptographicException)
            {
                // 凭据已成功,但初始整库同步/设备拉取失败。与 Unlock 失败路径一致地 Lock:清零刚解锁的
                // UserKey/tokens,并把状态收敛为 Locked。已保存的会话保留,用户可重新解锁重试(S7)。
                await LockAsync(CancellationToken.None).ConfigureAwait(false);
                return new AuthResult.Failure(
                    "登录成功,但加载密码库数据失败,请重新解锁重试。");
            }
        }
        finally
        {
            // 拉伸主密钥用完即弃:解包 UserKey 后不再需要,清零防止驻留内存(userKey 为独立数组,不受影响)。
            context.StretchedKey.Dispose();
        }
    }

    // 记住设备令牌只可在同 email + origin 下复用:本次响应有则用之;remember 登录服务端不回新令牌
    // (返回 null),此时保留旧值(仅当 origin+email 一致)不清空。
    private string? ResolveRememberToken(
        PendingTwoFactorContext context,
        TokenResponse token)
    {
        if (!string.IsNullOrEmpty(token.TwoFactorToken))
            return token.TwoFactorToken;
        return _tokenStore.TryLoad(out var prior)
            && prior.Origin == context.Origin
            && string.Equals(
                prior.Email,
                context.Email,
                StringComparison.OrdinalIgnoreCase)
            ? prior.TwoFactorToken
            : null;
    }

    private PendingTwoFactorContext BuildLoginContext(
        ServerOrigin origin,
        string email,
        string masterPassword,
        PreloginResponse prelogin,
        string deviceIdentifier)
    {
        var masterKey = _crypto.DeriveMasterKey(
            masterPassword,
            email,
            prelogin.Kdf,
            prelogin.KdfIterations,
            prelogin.KdfMemory,
            prelogin.KdfParallelism);
        try
        {
            var passwordHash = _crypto.ComputeMasterPasswordHash(masterKey, masterPassword);
            var stretchedKey = _crypto.StretchMasterKey(masterKey);
            return new PendingTwoFactorContext(
                origin,
                email,
                deviceIdentifier,
                passwordHash,
                stretchedKey,
                prelogin.Kdf,
                prelogin.KdfIterations,
                prelogin.KdfMemory,
                prelogin.KdfParallelism,
                Array.Empty<int>());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private SymmetricCryptoKey DeriveStretchedKey(
        string masterPassword,
        string email,
        KdfType kdfType,
        int iterations,
        int? memory,
        int? parallelism)
    {
        var masterKey = _crypto.DeriveMasterKey(masterPassword, email, kdfType, iterations, memory, parallelism);
        try
        {
            return _crypto.StretchMasterKey(masterKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private static AuthResult.Failure ToFailure(ConnectTokenResult result) => result switch
    {
        ConnectTokenResult.Error error => new AuthResult.Failure(error.Message),
        ConnectTokenResult.TwoFactorRequired => new AuthResult.Failure("Two-factor authentication is still required."),
        _ => new AuthResult.Failure("Authentication failed."),
    };

    private static string NewDeviceIdentifier() => Guid.NewGuid().ToString("D");

    private static string NormalizeServerUrl(string serverUrl) => serverUrl.TrimEnd('/');

    private static bool IsExpectedAuthException(Exception ex) =>
        ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or CryptographicException
            or FormatException
            or ArgumentException
            or InvalidDataException
            or NotSupportedException
            or NotImplementedException;

    private sealed record PendingTwoFactorContext(
        ServerOrigin Origin,
        string Email,
        string DeviceIdentifier,
        string PasswordHash,
        SymmetricCryptoKey StretchedKey,
        KdfType KdfType,
        int KdfIterations,
        int? KdfMemory,
        int? KdfParallelism,
        IReadOnlyList<int> Providers);
}
