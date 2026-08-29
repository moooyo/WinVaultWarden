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
    private readonly object _pendingGate = new();
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
    private IIdentityApiClient Identity(ServerEndpoints endpoints) =>
        _clients.GetAnonymous<IIdentityApiClient>(endpoints);

    public AuthenticationAttemptLease BeginAuthenticationAttempt()
    {
        var attempt = _session.BeginAuthenticationAttempt();
        DisposePendingTwoFactor();
        return attempt;
    }

    public void AbandonAuthenticationAttempt(AuthenticationAttemptLease attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        _session.AbandonAuthenticationAttempt(attempt);
        DisposePendingTwoFactor(attempt);
    }

    public Task<AuthResult> LoginAsync(
        string serverUrl,
        string email,
        string masterPassword,
        CancellationToken ct = default) =>
        LoginAsync(BeginAuthenticationAttempt(), serverUrl, email, masterPassword, ct);

    public async Task<AuthResult> LoginAsync(
        AuthenticationAttemptLease attempt,
        string serverUrl,
        string email,
        string masterPassword,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        PendingTwoFactorContext? context = null;
        try
        {
            if (!_session.IsCurrent(attempt))
                return AuthenticationExpired();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, attempt.CancellationToken);
            var endpoints = ServerEndpoints.FromWebVault(NormalizeServerUrl(serverUrl));
            var api = Identity(endpoints);
            var normalizedEmail = MasterPasswordSaltPolicy.Resolve(email, explicitSalt: null);

            // prelogin/token 返回前不得改动当前(可能属于账户 A 的)会话状态,也不得开启 B 的 lease。
            var prelogin = await api.PreloginAsync(normalizedEmail, linked.Token);
            if (!_session.IsCurrent(attempt))
                return AuthenticationExpired();

            // 同一账户二次登录:复用上次的 device identifier(服务端把"记住此设备"令牌按 device 存储,
            // 换新 id 会令令牌失效),并带上已存的 remember 令牌免交互 2FA。仅在同 origin + 同 email 下复用。
            var deviceIdentifier = NewDeviceIdentifier();
            TwoFactorPayload? rememberPayload = null;
            if (_tokenStore.TryLoad(out var existing)
                && existing.Endpoints == endpoints
                && string.Equals(
                    MasterPasswordSaltPolicy.NormalizeEmail(existing.Email),
                    normalizedEmail,
                    StringComparison.Ordinal))
            {
                deviceIdentifier = existing.DeviceIdentifier;
                if (!string.IsNullOrEmpty(existing.TwoFactorToken))
                    rememberPayload = new TwoFactorPayload(RememberTwoFactorProvider, existing.TwoFactorToken, false);
            }

            context = BuildLoginContext(attempt, endpoints, normalizedEmail, masterPassword, prelogin, deviceIdentifier);
            var result = await api.ConnectTokenAsync(ConnectTokenRequest.Password(
                normalizedEmail,
                context.PasswordHash,
                context.DeviceIdentifier,
                DeviceName,
                rememberPayload), linked.Token);

            if (!_session.IsCurrent(attempt))
                return AuthenticationExpired();

            var ownedContext = context;
            context = null;
            return await HandleLoginResultAsync(
                ownedContext,
                result,
                ct,
                autoSendEmail: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AbandonAuthenticationAttempt(attempt);
            throw;
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            AbandonAuthenticationAttempt(attempt);
            return ClassifyExpectedFailure(ex, ct);
        }
        finally
        {
            context?.StretchedKey.Dispose();
        }
    }

    public Task<AuthResult> SubmitTwoFactorAsync(
        string code,
        int provider,
        CancellationToken ct = default,
        bool rememberDevice = true)
    {
        var attempt = GetPendingTwoFactorAttempt();
        return attempt is null
            ? Task.FromResult<AuthResult>(SessionExpiredFailure())
            : SubmitTwoFactorAsync(attempt, code, provider, ct, rememberDevice);
    }

    public async Task<AuthResult> SubmitTwoFactorAsync(
        AuthenticationAttemptLease attempt,
        string code,
        int provider,
        CancellationToken ct = default,
        bool rememberDevice = true)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!TryGetPendingTwoFactor(attempt, out var context)
            || !_session.IsCurrent(attempt))
            return SessionExpiredFailure();

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, attempt.CancellationToken);
            var result = await Identity(context.Endpoints).ConnectTokenAsync(ConnectTokenRequest.Password(
                context.Email,
                context.PasswordHash,
                context.DeviceIdentifier,
                DeviceName,
                new TwoFactorPayload(provider, code, rememberDevice)), linked.Token);

            if (!_session.IsCurrent(attempt))
                return AuthenticationExpired();

            return await HandleLoginResultAsync(context with { Providers = Array.Empty<int>() }, result, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AbandonAuthenticationAttempt(attempt);
            throw;
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            if (!_session.IsCurrent(attempt))
            {
                AbandonAuthenticationAttempt(attempt);
                return SessionExpiredFailure();
            }
            return ClassifyExpectedFailure(ex, ct);
        }
    }

    public Task<AuthResult> UnlockAsync(
        string masterPassword,
        CancellationToken ct = default) =>
        UnlockAsync(BeginAuthenticationAttempt(), masterPassword, ct);

    public async Task<AuthResult> UnlockAsync(
        AuthenticationAttemptLease attempt,
        string masterPassword,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!_session.IsCurrent(attempt))
            return AuthenticationExpired();
        if (!_tokenStore.TryLoad(out var persisted) || string.IsNullOrEmpty(persisted.ProtectedUserKey))
        {
            AbandonAuthenticationAttempt(attempt);
            return new AuthResult.Failure(
                "No saved session.",
                AuthFailureKind.NoSavedSession);
        }

        SymmetricCryptoKey userKey;
        try
        {
            var stretchedKey = DeriveStretchedKey(
                masterPassword,
                persisted.EffectiveMasterPasswordSalt,
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
            if (!_session.TryRestorePersistedSession(attempt, persisted, out _))
                return AuthenticationExpired();
            return ex is CryptographicException or FormatException
                ? new AuthResult.Failure(
                    "The master password could not unlock the stored key.",
                    AuthFailureKind.InvalidCredentials)
                : ClassifyExpectedFailure(ex, ct);
        }

        return await FinishLocalUnlockAsync(attempt, persisted, userKey, ct);
    }

    public Task<AuthResult> UnlockWithPinAsync(
        string pin,
        CancellationToken ct = default) =>
        UnlockWithPinAsync(BeginAuthenticationAttempt(), pin, ct);

    public async Task<AuthResult> UnlockWithPinAsync(
        AuthenticationAttemptLease attempt,
        string pin,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!_session.IsCurrent(attempt))
            return AuthenticationExpired();
        if (!_tokenStore.TryLoad(out var persisted) || string.IsNullOrEmpty(persisted.PinProtectedUserKey))
        {
            AbandonAuthenticationAttempt(attempt);
            return SafeFailure("未设置 PIN。");
        }

        SymmetricCryptoKey userKey;
        try
        {
            var stretchedKey = DeriveStretchedKey(
                pin, MasterPasswordSaltPolicy.NormalizeEmail(persisted.Email), persisted.KdfType,
                persisted.KdfIterations, persisted.KdfMemory, persisted.KdfParallelism);
            try
            {
                userKey = _crypto.DecryptUserKey(stretchedKey, EncString.Parse(persisted.PinProtectedUserKey));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return HandleWrongPin(attempt, persisted);
            }
            finally
            {
                // 拉伸子钥用完清零;存入 session 的 userKey 是独立数组,不受影响。
                stretchedKey.Dispose();
            }
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            AbandonAuthenticationAttempt(attempt);
            return ClassifyExpectedFailure(ex, ct);
        }

        // 成功解包 → 清零失败计数,经 normalized 记录在解锁门禁内落盘。
        return await FinishLocalUnlockAsync(
            attempt,
            persisted with { PinFailedAttempts = 0 },
            userKey,
            ct);
    }

    // PIN 错误:经恢复出的 locked lease 门禁写回失败计数/清 PIN,绝不在 TryCommit 外触碰 token store。
    private AuthResult HandleWrongPin(
        AuthenticationAttemptLease attempt,
        PersistedSession persisted)
    {
        if (!_session.TryRestorePersistedSession(attempt, persisted, out var lease))
            return AuthenticationExpired();
        var n = persisted.PinFailedAttempts + 1;
        if (n >= 5)
        {
            _session.TryCommit(lease!, () => _tokenStore.Save(persisted with
            {
                PinProtectedUserKey = null,
                PinFailedAttempts = 0,
                Generation = lease!.Generation,
            }));
            return new AuthResult.PinCleared("PIN 已因多次错误被清除，请用主密码解锁。");
        }
        _session.TryCommit(lease!, () => _tokenStore.Save(persisted with
        {
            PinFailedAttempts = n,
            Generation = lease!.Generation,
        }));
        return SafeFailure($"PIN 错误，还可尝试 {5 - n} 次。");
    }

    public Task<AuthResult> UnlockWithHelloAsync(
        byte[] signature,
        CancellationToken ct = default) =>
        UnlockWithHelloAsync(BeginAuthenticationAttempt(), signature, ct);

    public async Task<AuthResult> UnlockWithHelloAsync(
        AuthenticationAttemptLease attempt,
        byte[] signature,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!_session.IsCurrent(attempt))
        {
            CryptographicOperations.ZeroMemory(signature);
            return AuthenticationExpired();
        }
        if (!_tokenStore.TryLoad(out var persisted) || string.IsNullOrEmpty(persisted.HelloProtectedUserKey))
        {
            CryptographicOperations.ZeroMemory(signature);
            AbandonAuthenticationAttempt(attempt);
            return SafeFailure("未启用 Windows Hello。");
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
                if (!_session.TryRestorePersistedSession(attempt, persisted, out var lease))
                    return AuthenticationExpired();
                _session.TryCommit(lease!, () => _tokenStore.Save(persisted with
                {
                    HelloProtectedUserKey = null,
                    HelloChallenge = null,
                    Generation = lease!.Generation,
                }));
                return SafeFailure("Windows Hello 解锁失败，请用主密码解锁。");
            }
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            AbandonAuthenticationAttempt(attempt);
            return ClassifyExpectedFailure(ex, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }

        return await FinishLocalUnlockAsync(attempt, persisted, userKey, ct);
    }

    // 主密码/PIN/Hello 三入口共用:恢复绑定 → 门禁落 UserKey + 归一化持久化 → 缓存即时出数据 →
    // 统一走 refresher/bootstrap;网络失败按有无缓存分流。禁止在此复制三套刷新/缓存/引导逻辑。
    private async Task<AuthResult> FinishLocalUnlockAsync(
        AuthenticationAttemptLease attempt,
        PersistedSession persisted,
        SymmetricCryptoKey userKey,
        CancellationToken ct)
    {
        if (!_session.TryRestorePersistedSession(attempt, persisted, out var lease))
        {
            userKey.Dispose();
            return AuthenticationExpired();
        }
        var normalized = persisted with { Generation = lease!.Generation };

        var transferred = false;
        var cacheGenerationCurrent = persisted.Generation == lease!.Generation;
        try
        {
            // Cache generation rebinding is an optimization. A persistence failure
            // makes the cache unavailable for this attempt but must not block refresh.
            if (!cacheGenerationCurrent)
                cacheGenerationCurrent = _cache.RebindGeneration(lease!);

            try
            {
                if (!_session.TryCommit(lease!, () =>
                {
                    // Persist the rotated generation before transferring key and refresh-token ownership.
                    _tokenStore.Save(normalized);
                    _session.SetUnlockedKey(userKey);
                    _session.SetRefreshTokenOnly(normalized.RefreshToken);
                    transferred = true;
                }))
                    return SessionExpiredFailure();
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                or CryptographicException)
            {
                _session.TryLockIfCurrent(lease!);
                return SafeFailure("无法安全保存会话，请检查磁盘权限后重试。");
            }
        }
        finally
        {
            if (!transferred)
                userKey.Dispose();
        }

        if (!_session.TryCopyUserKey(lease!, out var cacheKey))
            return SessionExpiredFailure();
        bool fromCache;
        using (cacheKey)
            fromCache = cacheGenerationCurrent
                && _cache.TryLoadInto(lease!, cacheKey!);

        try
        {
            if (!await _refresher.TryRefreshAsync(lease!, ct).ConfigureAwait(false))
                return FinishUnlockOffline(lease!, fromCache);

            await _bootstrapper.BootstrapAsync(lease!, ct).ConfigureAwait(false);
            return _session.IsUnlocked(lease!)
                ? new AuthResult.Success(lease!)
                : SessionExpiredFailure();
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            // 刷新成功但整库同步失败(如账户错配 InvalidDataException/网络抖动)→ 按有无缓存分流。
            return FinishUnlockOffline(lease!, fromCache);
        }
    }

    // 网络刷新/整库同步失败后的收尾:有缓存 → 保持已解锁状态(DataStale 已由 TryLoadInto 置 true),
    // 离线解锁成功;无缓存 → 回滚(Lock 清零 UserKey)并失败。两条分支都在租约门禁内提交。
    private AuthResult FinishUnlockOffline(VaultSessionLease lease, bool fromCache)
    {
        if (fromCache
            && _session.TryCommit(lease, () => _session.SetState(VaultState.Unlocked)))
            return new AuthResult.Success(lease);

        _session.TryLockIfCurrent(lease);
        return SafeFailure("无法连接服务器，且无离线缓存，请联网后重试。");
    }

    // core 原语:第一动作即失效旧 lease(rotation-first);不停止通知、不清剪贴板、不导航——
    // 第三层 App 生命周期协调器必须在外层阻止新敏感操作并完成 App 编排。
    public Task LockAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        LockCurrentSession();
        DisposePendingTwoFactor();
        return Task.CompletedTask;
    }

    public Task LockAsync(
        VaultSessionLease expectedSession,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedSession);
        ct.ThrowIfCancellationRequested();
        if (_session.TryCommit(expectedSession, LockCurrentSession))
            DisposePendingTwoFactor();
        return Task.CompletedTask;
    }

    private void LockCurrentSession()
    {
        var lockedLease = _session.Lock();
        if (lockedLease is not null)
        {
            _session.TryCommit(lockedLease, () =>
            {
                if (_tokenStore.TryLoad(out var persisted)
                    && persisted.AccountId == lockedLease.AccountId
                    && persisted.Endpoints == lockedLease.Endpoints)
                    _tokenStore.Save(persisted with
                    {
                        Generation = lockedLease.Generation,
                    });
                _cache.RebindGeneration(lockedLease);
            });
        }
    }

    public Task LogoutAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        LogoutCurrentSession();
        DisposePendingTwoFactor();
        return Task.CompletedTask;
    }

    public Task<bool> LogoutAsync(
        VaultSessionLease expectedSession,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedSession);
        ct.ThrowIfCancellationRequested();
        var committed = _session.TryCommit(expectedSession, LogoutCurrentSession);
        if (committed)
            DisposePendingTwoFactor();
        return Task.FromResult(committed);
    }

    private void LogoutCurrentSession()
    {
        _session.Logout();
        _tokenStore.Clear();
        _cache.Clear();
    }

    private void DisposePendingTwoFactor()
    {
        PendingTwoFactorContext? pending;
        lock (_pendingGate)
        {
            pending = _pendingTwoFactor;
            _pendingTwoFactor = null;
        }
        pending?.StretchedKey.Dispose();
    }

    private void DisposePendingTwoFactor(AuthenticationAttemptLease attempt)
    {
        PendingTwoFactorContext? pending = null;
        lock (_pendingGate)
        {
            if (_pendingTwoFactor?.Attempt == attempt)
            {
                pending = _pendingTwoFactor;
                _pendingTwoFactor = null;
            }
        }
        pending?.StretchedKey.Dispose();
    }

    private AuthenticationAttemptLease? GetPendingTwoFactorAttempt()
    {
        lock (_pendingGate)
            return _pendingTwoFactor?.Attempt;
    }

    private bool TryGetPendingTwoFactor(
        AuthenticationAttemptLease attempt,
        out PendingTwoFactorContext context)
    {
        lock (_pendingGate)
        {
            if (_pendingTwoFactor is { } pending && pending.Attempt == attempt)
            {
                context = pending;
                return true;
            }
        }
        context = null!;
        return false;
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
                lock (_pendingGate)
                {
                    if (_pendingTwoFactor?.Attempt == context.Attempt)
                        _pendingTwoFactor = null;
                }
                return await CompleteLoginAsync(context, success.Token, ct);
            case ConnectTokenResult.TwoFactorRequired twoFactor:
                var pending = context with { Providers = twoFactor.Providers.ToArray() };
                PendingTwoFactorContext? stale;
                lock (_pendingGate)
                {
                    stale = _pendingTwoFactor;
                    _pendingTwoFactor = pending;
                }
                if (stale is not null
                    && !ReferenceEquals(stale.StretchedKey, context.StretchedKey))
                    stale.StretchedKey.Dispose();
                // 仅在首次进入验证码阶段(LoginAsync 路径)自动发码;2FA 提交失败再次挑战时不重复发信(避免每次错码都发一封)。
                if (autoSendEmail && twoFactor.Providers.Contains(EmailTwoFactorProvider))
                    await TrySendTwoFactorEmailAsync(pending, ct);
                if (ct.IsCancellationRequested
                    || !_session.IsCurrent(context.Attempt))
                {
                    AbandonAuthenticationAttempt(context.Attempt);
                    return AuthenticationExpired();
                }
                return new AuthResult.TwoFactorRequired(twoFactor.Providers);
            default:
                if (!TryGetPendingTwoFactor(context.Attempt, out _))
                {
                    context.StretchedKey.Dispose();
                    _session.AbandonAuthenticationAttempt(context.Attempt);
                }
                return ToFailure(result);
        }
    }

    // 登录期发码:best-effort。失败(服务端未启用邮箱2FA / 网络抖动)不阻断进入验证码阶段,用户可点"重发"重试。
    private async Task TrySendTwoFactorEmailAsync(PendingTwoFactorContext context, CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, context.Attempt.CancellationToken);
            await Identity(context.Endpoints).SendTwoFactorEmailLoginAsync(
                context.Email,
                context.PasswordHash,
                context.DeviceIdentifier,
                linked.Token);
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
        }
    }

    public Task<bool> ResendTwoFactorEmailAsync(CancellationToken ct = default)
    {
        var attempt = GetPendingTwoFactorAttempt();
        return attempt is null
            ? Task.FromResult(false)
            : ResendTwoFactorEmailAsync(attempt, ct);
    }

    public async Task<bool> ResendTwoFactorEmailAsync(
        AuthenticationAttemptLease attempt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!TryGetPendingTwoFactor(attempt, out var context)
            || !_session.IsCurrent(attempt))
            return false;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, attempt.CancellationToken);
            await Identity(context.Endpoints).SendTwoFactorEmailLoginAsync(
                context.Email,
                context.PasswordHash,
                context.DeviceIdentifier,
                linked.Token);
            return true;
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            if (ct.IsCancellationRequested || !_session.IsCurrent(attempt))
                AbandonAuthenticationAttempt(attempt);
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
            if (!_session.IsCurrent(context.Attempt))
                return AuthenticationExpired();
            if (string.IsNullOrWhiteSpace(token.Key))
                return InvalidResponseFailure();
            // fail-closed:AccountId 只从 access token JWT 的 sub 派生,缺失/非法一律拒绝建立会话。
            if (!AccessTokenSubjectReader.TryReadAccountId(token.AccessToken, out var accountId))
                return InvalidResponseFailure();

            var userKey = _crypto.DecryptUserKey(context.StretchedKey, EncString.Parse(token.Key));
            if (!_session.TryBeginAccountSession(
                    context.Attempt,
                    accountId,
                    context.Email,
                    context.Endpoints,
                    out var lease))
            {
                userKey.Dispose();
                return AuthenticationExpired();
            }
            var persisted = new PersistedSession(
                PersistedSession.CurrentSchemaVersion,
                accountId,
                context.Endpoints,
                lease!.Generation,
                context.Email,
                context.DeviceIdentifier,
                token.RefreshToken,
                token.Key,
                context.KdfType,
                context.KdfIterations,
                context.KdfMemory,
                context.KdfParallelism,
                context.MasterPasswordSalt)
            {
                TwoFactorToken = ResolveRememberToken(context, token),
            };

            var transferred = false;
            try
            {
                if (!_session.TryCommit(lease!, () =>
                {
                    _tokenStore.Save(persisted);
                    _session.SetTokens(token.AccessToken, token.RefreshToken);
                    _session.SetUnlockedKey(userKey);
                    _session.SetEncryptedPrivateKey(token.PrivateKey);
                    transferred = true;
                }))
                    return SessionExpiredFailure();
            }
            finally
            {
                if (!transferred)
                    userKey.Dispose();
            }

            try
            {
                await _bootstrapper.BootstrapAsync(lease!, ct).ConfigureAwait(false);
                return _session.IsUnlocked(lease!)
                    ? new AuthResult.Success(lease!)
                    : SessionExpiredFailure();
            }
            catch (Exception ex) when (
                ex is OperationCanceledException
                    or HttpRequestException
                    or VaultApiException
                    or CryptographicException)
            {
                // 凭据已成功,但初始整库同步/设备拉取失败。与 Unlock 失败路径一致地 Lock:清零刚解锁的
                // UserKey/tokens,并把状态收敛为 Locked。已保存的会话保留,用户可重新解锁重试(S7)。
                await LockAsync(lease!, CancellationToken.None).ConfigureAwait(false);
                return SafeFailure(
                    "登录成功，但加载密码库数据失败，请重新解锁重试。");
            }
        }
        finally
        {
            // 拉伸主密钥用完即弃:解包 UserKey 后不再需要,清零防止驻留内存(userKey 为独立数组,不受影响)。
            context.StretchedKey.Dispose();
            _session.AbandonAuthenticationAttempt(context.Attempt);
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
            && prior.Endpoints == context.Endpoints
            && string.Equals(
                MasterPasswordSaltPolicy.NormalizeEmail(prior.Email),
                context.Email,
                StringComparison.Ordinal)
            ? prior.TwoFactorToken
            : null;
    }

    private PendingTwoFactorContext BuildLoginContext(
        AuthenticationAttemptLease attempt,
        ServerEndpoints endpoints,
        string email,
        string masterPassword,
        PreloginResponse prelogin,
        string deviceIdentifier)
    {
        var resolved = prelogin.Resolve(email);
        var masterKey = _crypto.DeriveMasterKey(
            masterPassword,
            resolved.MasterPasswordSalt,
            resolved.KdfType,
            resolved.Iterations,
            resolved.Memory,
            resolved.Parallelism);
        try
        {
            var passwordHash = _crypto.ComputeMasterPasswordHash(masterKey, masterPassword);
            var stretchedKey = _crypto.StretchMasterKey(masterKey);
            return new PendingTwoFactorContext(
                attempt,
                endpoints,
                email,
                deviceIdentifier,
                passwordHash,
                stretchedKey,
                resolved.KdfType,
                resolved.Iterations,
                resolved.Memory,
                resolved.Parallelism,
                resolved.MasterPasswordSalt,
                Array.Empty<int>());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private SymmetricCryptoKey DeriveStretchedKey(
        string masterPassword,
        string salt,
        KdfType kdfType,
        int iterations,
        int? memory,
        int? parallelism)
    {
        var masterKey = _crypto.DeriveMasterKey(masterPassword, salt, kdfType, iterations, memory, parallelism);
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
        ConnectTokenResult.Error { HttpStatus: 400 or 401 } => new AuthResult.Failure(
            "Authentication credentials were rejected.",
            AuthFailureKind.InvalidCredentials),
        ConnectTokenResult.Error => new AuthResult.Failure(
            "Authentication failed.",
            AuthFailureKind.Unspecified),
        ConnectTokenResult.TwoFactorRequired => new AuthResult.Failure(
            "Two-factor authentication is still required.",
            AuthFailureKind.InvalidCredentials),
        _ => new AuthResult.Failure(
            "Authentication failed.",
            AuthFailureKind.Unspecified),
    };

    private static AuthResult.Failure ClassifyExpectedFailure(
        Exception exception,
        CancellationToken callerToken)
    {
        if (RequestTimeoutClassifier.IsTimeout(exception, callerToken))
        {
            return new AuthResult.Failure(
                "The authentication request timed out.",
                AuthFailureKind.Timeout);
        }

        if (exception is HttpRequestException)
        {
            return new AuthResult.Failure(
                "The authentication server could not be reached.",
                AuthFailureKind.Network);
        }

        if (exception is TaskCanceledException)
            return SessionExpiredFailure();

        return InvalidResponseFailure();
    }

    private static AuthResult.Failure InvalidResponseFailure() =>
        new("The authentication response was invalid.", AuthFailureKind.InvalidResponse);

    private static AuthResult.Failure SessionExpiredFailure() =>
        new("The authentication session expired.", AuthFailureKind.SessionExpired);

    private static AuthResult.Failure SafeFailure(string message) =>
        new(message, AuthFailureKind.SafeMessage);

    private static AuthResult.Failure AuthenticationExpired() => SessionExpiredFailure();

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
        AuthenticationAttemptLease Attempt,
        ServerEndpoints Endpoints,
        string Email,
        string DeviceIdentifier,
        string PasswordHash,
        SymmetricCryptoKey StretchedKey,
        KdfType KdfType,
        int KdfIterations,
        int? KdfMemory,
        int? KdfParallelism,
        string MasterPasswordSalt,
        IReadOnlyList<int> Providers);
}
