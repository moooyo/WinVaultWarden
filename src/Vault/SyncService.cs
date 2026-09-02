using Api;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using System.Runtime.ExceptionServices;

namespace Vault;

public sealed class SyncService : ISyncService
{
    private const int MaximumSecurityStampLength = 128;

    private readonly IVaultApiClientProvider _clients;
    private readonly VaultDecryptor _decryptor;
    private readonly VaultSession _session;
    private readonly VaultCacheService _cache;
    private readonly ITokenStore? _tokenStore;
    private readonly ISessionTerminationSink? _termination;
    private readonly object _syncGateLock = new();
    private readonly List<LeaseSyncGate> _syncGates = [];

    public SyncService(
        IVaultApiClientProvider clients,
        VaultDecryptor decryptor,
        VaultSession session,
        VaultCacheService cache,
        ITokenStore? tokenStore = null,
        ISessionTerminationSink? termination = null)
    {
        _clients = clients;
        _decryptor = decryptor;
        _session = session;
        _cache = cache;
        _tokenStore = tokenStore;
        _termination = termination;
    }

    // Capture one lease at the compatibility boundary, then keep it for the full operation.
    public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default) =>
        SyncAsync(_session.CaptureLease(), ct);

    public async Task<IReadOnlyList<Cipher>> SyncAsync(
        VaultSessionLease lease,
        CancellationToken ct = default)
    {
        var result = await RunSyncAsync(lease, pending: null, ct).ConfigureAwait(false);
        return result.Ciphers;
    }

    public async Task<bool> SyncIfNeededAsync(
        VaultSessionLease lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        // Account revision-date omits some organization-scoped changes and cannot be safely
        // compared with a client wall-clock cache timestamp. Until the server exposes a complete
        // authoritative watermark, callers requesting catch-up must run the full sync pipeline.
        await SyncAsync(lease, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<CipherCreateReconciliationResult> SyncAndReconcileCipherCreateAsync(
        VaultSessionLease lease,
        CipherCreatePending pending,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var result = await RunSyncAsync(lease, pending, ct).ConfigureAwait(false);
        return result.Reconciliation
            ?? throw new InvalidOperationException("Cipher create reconciliation was not produced.");
    }

    private async Task<SyncCoreResult> RunSyncAsync(
        VaultSessionLease lease,
        CipherCreatePending? pending,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var syncGate = RentSyncGate(lease);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(
            ct, lease.CancellationToken);
        try
        {
            await syncGate.Semaphore.WaitAsync(admission.Token).ConfigureAwait(false);
            try
            {
                return await SyncCoreAsync(lease, pending, ct).ConfigureAwait(false);
            }
            finally
            {
                syncGate.Semaphore.Release();
            }
        }
        finally
        {
            ReturnSyncGate(syncGate);
        }
    }

    private LeaseSyncGate RentSyncGate(VaultSessionLease lease)
    {
        lock (_syncGateLock)
        {
            var syncGate = _syncGates.FirstOrDefault(
                candidate => candidate.Lease.SameSessionAs(lease));
            if (syncGate is null)
            {
                syncGate = new LeaseSyncGate(lease);
                _syncGates.Add(syncGate);
            }

            syncGate.ReferenceCount++;
            return syncGate;
        }
    }

    private void ReturnSyncGate(LeaseSyncGate syncGate)
    {
        lock (_syncGateLock)
        {
            syncGate.ReferenceCount--;
            if (syncGate.ReferenceCount == 0)
                _syncGates.Remove(syncGate);
        }
    }

    private async Task<SyncCoreResult> SyncCoreAsync(
        VaultSessionLease lease,
        CipherCreatePending? pending,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);

        // Give this operation an owned UserKey copy and reject expired leases.
        if (!_session.TryCopyUserKeyMaterial(lease, out var copiedKey))
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey;
        var syncStartedAtUtc = DateTimeOffset.UtcNow;

        // The linked token provides cooperative cancellation; TryCommit is the final lease guard.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, lease.CancellationToken);
        if (!_session.TrySetCapabilities(lease, VaultCapabilities.None))
            throw new OperationCanceledException(lease.CancellationToken);

        var api = _clients.GetAuthenticated<IReadonlyApiClient>(lease);
        var sync = await api.GetSyncAsync(linked.Token).ConfigureAwait(false);

        // Reject account-mismatched responses before committing cache or session state.
        if (!string.Equals(sync.Profile?.Id, lease.AccountId, StringComparison.Ordinal))
            throw new InvalidDataException("Sync profile does not match the active account.");

        var securityStamp = RequireSecurityStamp(sync.Profile);
        switch (_session.CompareSecurityStamp(lease, securityStamp))
        {
            case SecurityStampComparison.StaleLease:
                throw new OperationCanceledException(
                    "Sync result belongs to an expired session.",
                    lease.CancellationToken);
            case SecurityStampComparison.Mismatch:
                if (TryLockAndRemovePersistedSessionData(
                        lease,
                        out var invalidatedLease,
                        out var persistenceFailures))
                {
                    _termination?.Request(new SessionTerminationNotice(
                        SessionTerminationReason.InvalidSecurityStamp,
                        invalidatedLease!));

                    if (persistenceFailures.Count != 0)
                    {
                        throw new InvalidDataException(
                            "The account security stamp changed, but its saved session could not be removed completely.",
                            new AggregateException(persistenceFailures));
                    }

                    throw new InvalidDataException("The account security stamp changed.");
                }
                throw new OperationCanceledException(
                    "Sync result belongs to an expired session.",
                    lease.CancellationToken);
            case SecurityStampComparison.NoBaseline:
            case SecurityStampComparison.Match:
                break;
            default:
                throw new InvalidOperationException("Unknown security stamp comparison result.");
        }

        if (sync.Profile?.ForcePasswordReset == true)
        {
            if (TryLockAndRemovePersistedSessionData(
                    lease,
                    out var invalidatedLease,
                    out var persistenceFailures))
            {
                _termination?.Request(new SessionTerminationNotice(
                    SessionTerminationReason.RequiredPasswordChange,
                    invalidatedLease!));

                if (persistenceFailures.Count != 0)
                {
                    throw new InvalidDataException(
                        "The account requires a master-password change, but its saved session could not be removed completely.",
                        new AggregateException(persistenceFailures));
                }

                throw new InvalidDataException("The account requires a master-password change.");
            }
            throw new OperationCanceledException(
                "Sync result belongs to an expired session.",
                lease.CancellationToken);
        }

        var reconciliation = pending is null
            ? null
            : CipherCreateReconciler.Reconcile(
                pending,
                lease.AccountId,
                lease.Endpoints,
                (sync.Ciphers ?? Array.Empty<Api.Dtos.CipherDto>()).Select(cipher =>
                    new CipherCreateObservation(
                        cipher.Id,
                        (CipherType)cipher.Type,
                        cipher.OrganizationId,
                        cipher.CollectionIds ?? Array.Empty<string>(),
                        cipher.Key)));

        var config = await GetFreshConfigOrNullAsync(lease, linked.Token).ConfigureAwait(false);
        var capabilities = VaultCapabilityResolver.Resolve(config, sync.Profile, lease.Endpoints);
        var vault = _decryptor.Decrypt(sync, userKey!, lease.Endpoints);
        var removePinUnlock = _tokenStore is not null
            && VaultPolicyEvaluator.IsEnabledForUser(
                OrganizationPolicyType.RemoveUnlockWithPin,
                vault.PolicyOrganizations ?? Array.Empty<VaultOrganizationPolicyContext>(),
                vault.Policies);

        // PIN revocation precedes cache persistence so a failed cache write cannot preserve an
        // offline PIN that the authoritative policy has removed. Session publication remains last.
        try
        {
            if (!_session.TryCommitSecurityStamp(lease, securityStamp, () =>
            {
                PersistSyncInputsForCommit(
                    lease,
                    sync,
                    vault,
                    removePinUnlock,
                    syncStartedAtUtc);
                _session.SetEncryptedPrivateKey(vault.WrappedAccountPrivateKey);
                _session.SetSnapshot(vault, dataStale: false, capabilities: capabilities);
            }))
                throw new OperationCanceledException(
                    "Sync result belongs to an expired session.",
                    lease.CancellationToken);
        }
        catch when (removePinUnlock)
        {
            if (_session.TryLockIfCurrent(lease, out var lockedLease))
            {
                _termination?.Request(new SessionTerminationNotice(
                    SessionTerminationReason.PinPolicyEnforcementFailed,
                    lockedLease!));
            }
            throw;
        }

        return new SyncCoreResult(vault.Ciphers, reconciliation);
    }

    private static string RequireSecurityStamp(Api.Dtos.ProfileDto? profile)
    {
        var securityStamp = profile?.SecurityStamp;
        if (string.IsNullOrWhiteSpace(securityStamp)
            || securityStamp.Length > MaximumSecurityStampLength
            || !string.Equals(securityStamp, securityStamp.Trim(), StringComparison.Ordinal)
            || securityStamp.Any(char.IsControl))
        {
            throw new InvalidDataException("Sync profile has an invalid security stamp.");
        }

        return securityStamp;
    }

    private bool TryLockAndRemovePersistedSessionData(
        VaultSessionLease lease,
        out VaultSessionLease? invalidatedLease,
        out List<Exception> persistenceFailures)
    {
        VaultSessionLease? claimedLease = null;
        List<Exception>? collectedFailures = null;
        var claimed = _session.TryCommit(lease, () =>
        {
            // The account-scoped stores do not accept a generation. Keep the session gate held
            // from lease claim through rotation and purge so an old response cannot delete a
            // newly persisted same-account session.
            claimedLease = _session.Lock();
            collectedFailures = RemovePersistedSessionData(lease);
        });

        invalidatedLease = claimedLease;
        persistenceFailures = collectedFailures ?? [];
        return claimed;
    }

    private List<Exception> RemovePersistedSessionData(VaultSessionLease lease)
    {
        var failures = new List<Exception>();

        if (_tokenStore is not null)
        {
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
        }

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

    private void PersistSyncInputsForCommit(
        VaultSessionLease lease,
        Api.Dtos.SyncResponse sync,
        DecryptedVault vault,
        bool removePinUnlock,
        DateTimeOffset syncStartedAtUtc)
    {
        if (!removePinUnlock)
        {
            _cache.SaveCurrent(lease, sync, syncStartedAtUtc);
            return;
        }

        var failures = new List<Exception>();
        Exception? initialCacheIsolationFailure = null;
        try
        {
            _cache.Clear(lease.AccountId, lease.Endpoints);
        }
        catch (Exception exception)
        {
            initialCacheIsolationFailure = exception;
        }

        var pinRevocationFailed = false;
        try
        {
            RemovePinUnlockIfRequired(lease, vault);
        }
        catch (Exception exception)
        {
            pinRevocationFailed = true;
            failures.Add(exception);
        }

        try
        {
            _cache.SaveCurrent(lease, sync, syncStartedAtUtc);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count == 0)
            return;

        if (initialCacheIsolationFailure is not null)
            failures.Add(initialCacheIsolationFailure);

        if (pinRevocationFailed)
        {
            try
            {
                if (!_tokenStore!.Remove(lease.AccountId, lease.Endpoints))
                {
                    failures.Add(new InvalidDataException(
                        "The PIN-bearing persisted session could not be removed."));
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        try
        {
            _cache.Clear(lease.AccountId, lease.Endpoints);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(
            "PIN policy enforcement and persisted-session isolation failed.",
            failures);
    }

    private async Task<Api.Dtos.ConfigResponse?> GetFreshConfigOrNullAsync(
        VaultSessionLease lease,
        CancellationToken ct)
    {
        try
        {
            var configApi = _clients.GetAuthenticated<IServerConfigApiClient>(lease);
            return await configApi.GetConfigAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private void RemovePinUnlockIfRequired(VaultSessionLease lease, DecryptedVault vault)
    {
        if (_tokenStore is null
            || !VaultPolicyEvaluator.IsEnabledForUser(
                OrganizationPolicyType.RemoveUnlockWithPin,
                vault.PolicyOrganizations ?? Array.Empty<VaultOrganizationPolicyContext>(),
                vault.Policies))
        {
            return;
        }

        if (!_tokenStore.TryLoad(out var persisted)
            || persisted.AccountId != lease.AccountId
            || persisted.Endpoints != lease.Endpoints
            || string.IsNullOrEmpty(persisted.PinProtectedUserKey))
            return;

        _tokenStore.Save(persisted with
        {
            Generation = lease.Generation,
            PinProtectedUserKey = null,
            PinFailedAttempts = 0,
        });
    }

    private sealed class LeaseSyncGate(VaultSessionLease lease)
    {
        public VaultSessionLease Lease { get; } = lease;
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private sealed record SyncCoreResult(
        IReadOnlyList<Cipher> Ciphers,
        CipherCreateReconciliationResult? Reconciliation);
}
