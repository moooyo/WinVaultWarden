using System.Net.Http;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Services;
using Core.Session;

namespace Vault;

public sealed class TokenRefreshService : ITokenRefresher
{
    private const string DeviceName = "WinVaultWarden";

    private readonly IVaultApiClientProvider _clients;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;
    private readonly ISessionTerminationSink? _termination;
    private readonly VaultCacheService? _cache;
    private readonly IAccountDeletionTombstoneStore? _accountDeletionTombstones;
    private readonly IAccountDeletionCoordinationGate _accountDeletionCoordinationGate;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TokenRefreshService(
        IVaultApiClientProvider clients,
        VaultSession session,
        ITokenStore tokenStore,
        ISessionTerminationSink? termination = null,
        VaultCacheService? cache = null,
        IAccountDeletionTombstoneStore? accountDeletionTombstones = null,
        IAccountDeletionCoordinationGate? accountDeletionCoordinationGate = null)
    {
        _clients = clients;
        _session = session;
        _tokenStore = tokenStore;
        _termination = termination;
        _cache = cache;
        _accountDeletionTombstones = accountDeletionTombstones;
        _accountDeletionCoordinationGate = accountDeletionCoordinationGate
            ?? NoopAccountDeletionCoordinationGate.Instance;
    }

    public async Task<bool> TryRefreshAsync(
        VaultSessionLease lease,
        CancellationToken ct = default) =>
        await TryRefreshCoreAsync(lease, null, ct).ConfigureAwait(false);

    public async Task<bool> TryRefreshAsync(
        VaultSessionLease lease,
        AccessTokenBinding failedAuthorization,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(failedAuthorization);
        if (failedAuthorization.AccountId != lease.AccountId
            || failedAuthorization.Endpoints != lease.Endpoints
            || failedAuthorization.Generation != lease.Generation)
            return false;

        return await TryRefreshCoreAsync(
            lease,
            failedAuthorization,
            ct).ConfigureAwait(false);
    }

    private async Task<bool> TryRefreshCoreAsync(
        VaultSessionLease lease,
        AccessTokenBinding? failedAuthorization,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);

        if (!_session.TryGetTokenSnapshot(
                lease, out var beforeAccess, out var refreshToken)
            || string.IsNullOrWhiteSpace(refreshToken))
            return false;

        // 只有 caller 的 token 取消才向上传播;lease 过期导致的取消返回 false,不重试旧请求。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, lease.CancellationToken);
        IAccountDeletionCoordinationLease deletionCoordination;
        try
        {
            deletionCoordination = await _accountDeletionCoordinationGate
                .AcquireAsync(linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException)
        {
            return false;
        }
        await using var deletionCoordinationScope = deletionCoordination;

        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        try
        {
            if (!_session.TryGetTokenSnapshot(
                    lease, out var currentAccess, out refreshToken))
                return false;
            var failedToken = failedAuthorization?.Token ?? beforeAccess;
            if (!string.Equals(currentAccess, failedToken, StringComparison.Ordinal))
                return true;
            if (string.IsNullOrWhiteSpace(refreshToken)
                || IsAccountDeletionBlocked(lease)
                || !_tokenStore.TryLoad(out var persisted)
                || !persisted.HasValidBinding
                || persisted.AccountId != lease.AccountId
                || persisted.Endpoints != lease.Endpoints
                || persisted.Generation != lease.Generation)
                return false;

            var api = _clients.GetAnonymous<IIdentityApiClient>(lease.Endpoints);
            linked.Token.ThrowIfCancellationRequested();
            // Once dispatched, a refresh may already have consumed the old token. Continue
            // reading the bounded control-plane response so a concurrent lock cannot lose
            // the rotated token; lease and account gates still control every local commit.
            var result = await api.ConnectTokenAsync(
                ConnectTokenRequest.Refresh(
                    refreshToken!,
                    persisted.DeviceIdentifier,
                    DeviceName),
                CancellationToken.None).ConfigureAwait(false);

            if (result is ConnectTokenResult.Success success)
            {
                if (string.IsNullOrWhiteSpace(success.Token.RefreshToken)
                    || !AccessTokenSubjectReader.TryReadAccountId(
                        success.Token.AccessToken,
                        out var refreshedAccountId)
                    || !string.Equals(
                        refreshedAccountId,
                        lease.AccountId,
                        StringComparison.Ordinal))
                {
                    return false;
                }
                if (IsAccountDeletionBlocked(lease))
                    return false;
                if (_session.TryCommit(lease, () =>
                {
                    _tokenStore.Save(persisted with
                    {
                        RefreshToken = success.Token.RefreshToken,
                        Generation = lease.Generation,
                        HasMasterPassword = success.Token.UserDecryptionOptions?.HasMasterPassword
                            ?? persisted.HasMasterPassword,
                    });
                    _session.SetTokens(
                        success.Token.AccessToken,
                        success.Token.RefreshToken);
                }))
                {
                    return true;
                }

                TryPreserveRotatedRefreshAfterLock(
                    lease,
                    refreshToken!,
                    success.Token);
                return false;
            }

            // B1：仅当服务端明确判定 refresh token 失效（invalid_grant，见 vaultwarden identity.rs）
            // 且原始 lease 仍 current 时，rotation-first 锁定当前会话并清敏感状态。
            // 陈旧 lease 的响应（stale A）绝不锁定已切换的新会话（B）。
            if (result is ConnectTokenResult.Error { ErrorCode: "invalid_grant" })
            {
                VaultSessionLease? invalidatedLease = null;
                List<Exception>? persistenceFailures = null;
                var claimed = _session.TryCommit(lease, () =>
                {
                    // Keep the session gate held from lease claim through account-scoped purge.
                    // Otherwise a same-account re-login could persist a newer generation between
                    // Lock and Remove, and the old invalid-grant response would delete it.
                    invalidatedLease = _session.Lock();
                    persistenceFailures = RemovePersistedSessionData(lease);
                });
                if (claimed)
                {
                    try
                    {
                        _termination?.Request(new SessionTerminationNotice(
                            SessionTerminationReason.InvalidRefreshToken,
                            invalidatedLease!));
                    }
                    finally
                    {
                        if (persistenceFailures is { Count: > 0 })
                        {
                            throw new AuthSessionPersistenceException(
                                "The invalid session could not be removed securely.",
                                new AggregateException(persistenceFailures));
                        }
                    }
                }
            }
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or JsonException
                or IOException
                or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryPreserveRotatedRefreshAfterLock(
        VaultSessionLease staleLease,
        string consumedRefreshToken,
        TokenResponse token)
    {
        VaultSessionLease currentLease;
        try
        {
            currentLease = _session.CaptureLease();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (currentLease.AccountId != staleLease.AccountId
            || currentLease.Endpoints != staleLease.Endpoints)
        {
            return false;
        }

        var preserved = false;
        return _session.TryCommit(currentLease, () =>
        {
            var locked = _session.State == VaultState.Locked;
            var resumingFromOfflineUnlock = _session.State == VaultState.Unlocked
                && _session.AccessToken is null
                && string.Equals(
                    _session.RefreshToken,
                    consumedRefreshToken,
                    StringComparison.Ordinal);
            if (!locked && !resumingFromOfflineUnlock)
                return;
            if (!_tokenStore.TryLoad(out var latest)
                || !latest.HasValidBinding
                || latest.AccountId != currentLease.AccountId
                || latest.Endpoints != currentLease.Endpoints
                || !string.Equals(
                    latest.RefreshToken,
                    consumedRefreshToken,
                    StringComparison.Ordinal))
            {
                return;
            }

            _tokenStore.Save(latest with
            {
                RefreshToken = token.RefreshToken,
                Generation = currentLease.Generation,
                HasMasterPassword = token.UserDecryptionOptions?.HasMasterPassword
                    ?? latest.HasMasterPassword,
            });
            if (resumingFromOfflineUnlock)
            {
                _session.SetTokens(
                    token.AccessToken,
                    token.RefreshToken);
            }
            preserved = true;
        }) && preserved;
    }

    private bool IsAccountDeletionBlocked(VaultSessionLease lease)
    {
        if (_accountDeletionTombstones is null)
            return false;
        try
        {
            var read = _accountDeletionTombstones.ReadAll();
            return read.Status is AccountDeletionTombstoneReadStatus.Corrupt
                    or AccountDeletionTombstoneReadStatus.Unavailable
                || read.Status == AccountDeletionTombstoneReadStatus.Found
                && read.Records.Any(record =>
                    record.Binding.AccountId == lease.AccountId
                    && record.Binding.Endpoints == lease.Endpoints);
        }
        catch
        {
            return true;
        }
    }

    private List<Exception> RemovePersistedSessionData(VaultSessionLease lease)
    {
        var failures = new List<Exception>();
        try
        {
            if (!_tokenStore.Remove(lease.AccountId, lease.Endpoints))
            {
                failures.Add(new InvalidDataException(
                    "The persisted session could not be removed."));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failures.Add(exception);
        }

        if (_cache is null)
            return failures;

        try
        {
            _cache.Clear(lease.AccountId, lease.Endpoints);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failures.Add(exception);
        }

        return failures;
    }
}

