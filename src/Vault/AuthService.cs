using System.Security.Cryptography;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
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

    // 匿名 identity 角色按 serverUrl 派生的 origin 获取;登录/刷新/发码全部走匿名 transport,绝不携带 Bearer。
    private IIdentityApiClient Identity(string serverUrl) =>
        _clients.GetAnonymous<IIdentityApiClient>(new ServerOrigin(serverUrl));

    public async Task<AuthResult> LoginAsync(
        string serverUrl,
        string email,
        string masterPassword,
        CancellationToken ct = default)
    {
        try
        {
            var normalizedServerUrl = NormalizeServerUrl(serverUrl);
            var api = Identity(normalizedServerUrl);
            _session.SetState(Core.Session.VaultState.Unlocking);

            var prelogin = await api.PreloginAsync(email, ct);

            // 同一账户二次登录:复用上次的 device identifier(服务端把"记住此设备"令牌按 device 存储,
            // 换新 id 会令令牌失效),并带上已存的 remember 令牌免交互 2FA。
            var deviceIdentifier = NewDeviceIdentifier();
            TwoFactorPayload? rememberPayload = null;
            if (_tokenStore.TryLoad(out var existing)
                && string.Equals(existing.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                deviceIdentifier = existing.DeviceIdentifier;
                if (!string.IsNullOrEmpty(existing.TwoFactorToken))
                    rememberPayload = new TwoFactorPayload(RememberTwoFactorProvider, existing.TwoFactorToken, false);
            }

            var context = BuildLoginContext(normalizedServerUrl, email, masterPassword, prelogin, deviceIdentifier);
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
            _session.SetState(Core.Session.VaultState.Error);
            return new AuthResult.Failure(ex.Message);
        }
    }

    public async Task<AuthResult> SubmitTwoFactorAsync(string code, int provider, CancellationToken ct = default, bool rememberDevice = true)
    {
        if (_pendingTwoFactor is not { } context)
            return new AuthResult.Failure("No two-factor challenge is pending.");

        try
        {
            var result = await Identity(context.ServerUrl).ConnectTokenAsync(ConnectTokenRequest.Password(
                context.Email,
                context.PasswordHash,
                context.DeviceIdentifier,
                DeviceName,
                new TwoFactorPayload(provider, code, rememberDevice)), ct);

            return await HandleLoginResultAsync(context with { Providers = Array.Empty<int>() }, result, ct);
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            _session.SetState(Core.Session.VaultState.Error);
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
            _session.SetState(Core.Session.VaultState.Unlocking);

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

        // 本地已可离线解锁:先落 UserKey,再尝试从加密缓存立即出数据;随后联网刷新 token + 整库同步
        // (可能因断网/服务端不可达而失败——失败时按有无缓存分流,见 FinishUnlockOffline)。
        _session.RestorePersistedSession(persisted);
        _session.SetUnlockedKey(userKey);
        var fromCache = _cache.TryLoadInto(persisted.Email, persisted.ServerUrl, userKey);

        try
        {
            var refresh = await Identity(persisted.ServerUrl).ConnectTokenAsync(ConnectTokenRequest.Refresh(
                persisted.RefreshToken,
                persisted.DeviceIdentifier,
                DeviceName), ct);

            if (refresh is not ConnectTokenResult.Success success)
                return FinishUnlockOffline(fromCache, persisted.RefreshToken);

            _session.SetTokens(success.Token.AccessToken, success.Token.RefreshToken);
            _tokenStore.Save(persisted with { RefreshToken = success.Token.RefreshToken });
            await _bootstrapper.BootstrapAsync(persisted.ServerUrl, ct);
            return new AuthResult.Success();
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            return FinishUnlockOffline(fromCache, persisted.RefreshToken);
        }
    }

    // 网络刷新/整库同步失败后的收尾:有缓存 → 保持已解锁状态,快照来自缓存(DataStale 已由
    // VaultCacheService.TryLoadInto 置 true),离线解锁成功;无缓存 → 回滚(Lock 清零 UserKey)并失败。
    private AuthResult FinishUnlockOffline(bool fromCache, string refreshToken)
    {
        if (fromCache)
        {
            // B2:把持久化 refresh token 种回内存,使联网后 401 能触发真实刷新、透明转在线。
            _session.SetRefreshTokenOnly(refreshToken);
            _session.SetState(Core.Session.VaultState.Unlocked);
            return new AuthResult.Success();
        }
        _session.Lock();
        return new AuthResult.Failure("无法连接服务器,且无离线缓存,请联网后重试。");
    }

    public async Task<AuthResult> UnlockWithPinAsync(string pin, CancellationToken ct = default)
    {
        if (!_tokenStore.TryLoad(out var persisted) || string.IsNullOrEmpty(persisted.PinProtectedUserKey))
            return new AuthResult.Failure("未设置 PIN。");

        SymmetricCryptoKey userKey;
        try
        {
            _session.SetState(Core.Session.VaultState.Unlocking);

            var stretchedKey = DeriveStretchedKey(
                pin, persisted.Email, persisted.KdfType,
                persisted.KdfIterations, persisted.KdfMemory, persisted.KdfParallelism);
            try
            {
                userKey = _crypto.DecryptUserKey(stretchedKey, EncString.Parse(persisted.PinProtectedUserKey));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                _session.Lock();
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

        // 本地已可离线解锁:先落 UserKey,再尝试从加密缓存立即出数据;随后联网刷新 token + 整库同步
        // (可能因断网/服务端不可达而失败——失败时按有无缓存分流,见 FinishUnlockOffline)。
        _session.RestorePersistedSession(persisted);
        _session.SetUnlockedKey(userKey);
        var fromCache = _cache.TryLoadInto(persisted.Email, persisted.ServerUrl, userKey);

        try
        {
            var refresh = await Identity(persisted.ServerUrl).ConnectTokenAsync(ConnectTokenRequest.Refresh(
                persisted.RefreshToken, persisted.DeviceIdentifier, DeviceName), ct);

            if (refresh is not ConnectTokenResult.Success success)
                return FinishUnlockOffline(fromCache, persisted.RefreshToken);

            _session.SetTokens(success.Token.AccessToken, success.Token.RefreshToken);
            _tokenStore.Save(persisted with { RefreshToken = success.Token.RefreshToken, PinFailedAttempts = 0 });
            await _bootstrapper.BootstrapAsync(persisted.ServerUrl, ct);
            return new AuthResult.Success();
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            return FinishUnlockOffline(fromCache, persisted.RefreshToken);
        }
    }

    private AuthResult HandleWrongPin(PersistedSession persisted)
    {
        var n = persisted.PinFailedAttempts + 1;
        if (n >= 5)
        {
            _tokenStore.Save(persisted with { PinProtectedUserKey = null, PinFailedAttempts = 0 });
            return new AuthResult.PinCleared("PIN 已因多次错误被清除，请用主密码解锁。");
        }
        _tokenStore.Save(persisted with { PinFailedAttempts = n });
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
            _session.SetState(Core.Session.VaultState.Unlocking);

            using var helloKey = _crypto.DeriveKeyFromSecret(signature);
            try
            {
                userKey = _crypto.DecryptUserKey(helloKey, EncString.Parse(persisted.HelloProtectedUserKey));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                _session.Lock();
                // Hello 凭据失效(重录/移除)或数据损坏 → 清除 Hello,回退主密码。
                _tokenStore.Save(persisted with { HelloProtectedUserKey = null, HelloChallenge = null });
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

        // 本地已可离线解锁:先落 UserKey,再尝试从加密缓存立即出数据;随后联网刷新 token + 整库同步
        // (可能因断网/服务端不可达而失败——失败时按有无缓存分流,见 FinishUnlockOffline)。收尾与
        // UnlockWithPinAsync 完全一致。
        _session.RestorePersistedSession(persisted);
        _session.SetUnlockedKey(userKey);
        var fromCache = _cache.TryLoadInto(persisted.Email, persisted.ServerUrl, userKey);

        try
        {
            var refresh = await Identity(persisted.ServerUrl).ConnectTokenAsync(ConnectTokenRequest.Refresh(
                persisted.RefreshToken, persisted.DeviceIdentifier, DeviceName), ct);

            if (refresh is not ConnectTokenResult.Success success)
                return FinishUnlockOffline(fromCache, persisted.RefreshToken);

            _session.SetTokens(success.Token.AccessToken, success.Token.RefreshToken);
            _tokenStore.Save(persisted with { RefreshToken = success.Token.RefreshToken });
            await _bootstrapper.BootstrapAsync(persisted.ServerUrl, ct);
            return new AuthResult.Success();
        }
        catch (Exception ex) when (IsExpectedAuthException(ex))
        {
            return FinishUnlockOffline(fromCache, persisted.RefreshToken);
        }
    }

    public Task LockAsync(CancellationToken ct = default)
    {
        _session.Lock();
        return Task.CompletedTask;
    }

    public Task LogoutAsync(CancellationToken ct = default)
    {
        _tokenStore.Clear();
        _session.Clear();
        DisposePendingTwoFactor();
        _cache.Clear();
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
            await Identity(context.ServerUrl).SendTwoFactorEmailLoginAsync(context.Email, context.PasswordHash, context.DeviceIdentifier, ct);
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
            await Identity(context.ServerUrl).SendTwoFactorEmailLoginAsync(context.Email, context.PasswordHash, context.DeviceIdentifier, ct);
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

            var userKey = _crypto.DecryptUserKey(context.StretchedKey, EncString.Parse(token.Key));
            // 保存"记住此设备"令牌:本次响应有则用之;remember 登录时服务端不回新令牌(返回 null),此时保留旧值不清空。
            var rememberToken = token.TwoFactorToken;
            if (rememberToken is null
                && _tokenStore.TryLoad(out var prior)
                && string.Equals(prior.Email, context.Email, StringComparison.OrdinalIgnoreCase))
            {
                rememberToken = prior.TwoFactorToken;
            }
            _session.BeginAccountSession(context.Email, context.Email, new ServerOrigin(context.ServerUrl));
            _session.SetTokens(token.AccessToken, token.RefreshToken);
            _session.SetUnlockedKey(userKey);
            _tokenStore.Save(new PersistedSession(
                context.ServerUrl,
                context.Email,
                context.DeviceIdentifier,
                token.RefreshToken,
                token.Key,
                context.KdfType,
                context.KdfIterations,
                context.KdfMemory,
                context.KdfParallelism)
            {
                TwoFactorToken = rememberToken,
            });
            try
            {
                await _bootstrapper.BootstrapAsync(context.ServerUrl, ct);
            }
            catch (Exception)
            {
                // 凭据已成功,但初始整库同步/设备拉取失败(网络抖动/取消/任意异常)。与 Unlock 失败路径一致地
                // Lock():清零刚解锁的 UserKey/tokens,避免在报"失败"后仍残留内存,并把状态收敛为 Locked
                // (而非悬挂的 Error)。已保存的会话保留,用户可重新解锁重试(S7)。
                _session.Lock();
                return new AuthResult.Failure("登录成功,但加载密码库数据失败,请重新解锁重试。");
            }
            return new AuthResult.Success();
        }
        finally
        {
            // 拉伸主密钥用完即弃:解包 UserKey 后不再需要,清零防止驻留内存(userKey 为独立数组,不受影响)。
            context.StretchedKey.Dispose();
        }
    }

    private PendingTwoFactorContext BuildLoginContext(
        string serverUrl,
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
                serverUrl,
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
            or NotSupportedException
            or NotImplementedException;

    private sealed record PendingTwoFactorContext(
        string ServerUrl,
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
