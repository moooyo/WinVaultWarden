using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using System.Runtime.ExceptionServices;

namespace Vault;

// 写操作编排器:加密(如需)→ 调用写 API → 整库重新同步。
// 写后整库重拉,保证本地快照与服务端一致(设计决策,见 spec)。
public sealed class VaultWriteService : IVaultWriteService
{
    private readonly IVaultApiClientProvider _clients;
    private readonly CipherEncryptor _encryptor;
    private readonly ISyncService _sync;
    private readonly VaultSession _session;
    private readonly OrganizationKeyResolver _organizationKeys;
    private readonly ICipherCreatePendingStore _cipherCreateStore;
    private readonly object _cipherCreateGate = new();
    private readonly Dictionary<CipherCreateScope, CipherCreateGateEntry> _cipherCreates = [];

    public event EventHandler? CipherCreateGateChanged;

    public VaultWriteService(
        IVaultApiClientProvider clients,
        CipherEncryptor encryptor,
        ISyncService sync,
        VaultSession session,
        OrganizationKeyResolver? organizationKeys = null,
        ICipherCreatePendingStore? cipherCreateStore = null)
    {
        _clients = clients;
        _encryptor = encryptor;
        _sync = sync;
        _session = session;
        _organizationKeys = organizationKeys ?? new OrganizationKeyResolver(new CryptoService());
        _cipherCreateStore = cipherCreateStore ?? UnavailableCipherCreatePendingStore.Instance;
    }

    public Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default) =>
        SaveCipherAsync(cipher, _session.CaptureLease(), ct);

    public Task<string?> SaveCipherAndGetIdAsync(Cipher cipher, CancellationToken ct = default) =>
        SaveCipherCoreAsync(cipher, _session.CaptureLease(), ct);

    // lease-bound:以调用方传入的原始 lease 门禁。跨加密/写 API/同步的每个 await 边界前后都校验
    // 该 lease 仍对应当前会话——一旦 Lock/切换账户使其失效,立即抛 OperationCanceledException,
    // 绝不把凭据写入或同步到别的账户(A 不能写 B)。
    public async Task SaveCipherAsync(Cipher cipher, VaultSessionLease lease, CancellationToken ct = default) =>
        _ = await SaveCipherCoreAsync(cipher, lease, ct);

    private async Task<string?> SaveCipherCoreAsync(
        Cipher cipher,
        VaultSessionLease lease,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        ArgumentNullException.ThrowIfNull(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        _session.ThrowIfExpired(lease);
        var capabilities = _session.GetCapabilities(lease);
        var isCreate = string.IsNullOrEmpty(cipher.Id);
        var openingSnapshot = _session.GetCiphers(lease);
        var current = isCreate
            ? null
            : openingSnapshot.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, cipher.Id, StringComparison.Ordinal));
        if (current is not null)
            CipherWritePolicy.EnsureCanWrite(current, capabilities);
        CipherWritePolicy.EnsureCanWrite(cipher, capabilities);
        if (!_session.TryCopyUserKeyMaterial(lease, out var userKey) || userKey is null)
            throw new OperationCanceledException(lease.CancellationToken);

        CipherRequest request;
        using (userKey)
        {
            if (VaultPolicyEvaluator.IsCipherRestricted(_session, cipher))
            {
                throw new OrganizationPolicyException(
                    "An organization policy restricts this item type.");
            }
            if (string.IsNullOrWhiteSpace(cipher.OrganizationId))
            {
                VaultPolicyEvaluator.ThrowIfEnabled(
                    _session,
                    Core.Enums.OrganizationPolicyType.PersonalOwnership,
                    "An organization policy prevents saving items to the personal vault.");
            }

            ValidateOwnershipAndWriteAccess(cipher, current);
            if (current is null && !string.IsNullOrWhiteSpace(cipher.OrganizationId))
                ValidateNewOrganizationAssignment(cipher, lease);

            SymmetricCryptoKey? organizationKey = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(cipher.OrganizationId))
                {
                    var organization = _session.Organizations.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, cipher.OrganizationId, StringComparison.Ordinal))
                        ?? throw new OrganizationCipherOperationException(
                            "The organization is not available in the current vault snapshot.");
                    if (!organization.IsMember)
                    {
                        throw new OrganizationCipherOperationException(
                            "Provider-only organizations are not writable vault destinations.");
                    }
                    _session.ThrowIfExpired(lease);
                    if (!_session.TryGetEncryptedPrivateKey(lease, out var encryptedPrivateKey))
                        throw new OperationCanceledException(lease.CancellationToken);
                    organizationKey = userKey.Legacy is { } legacy
                        ? _organizationKeys.Resolve(
                            organization,
                            encryptedPrivateKey,
                            legacy)
                        : _organizationKeys.Resolve(
                            organization,
                            encryptedPrivateKey,
                            userKey.V2!);
                    request = _encryptor.Encrypt(cipher, organizationKey, capabilities);
                }
                else
                {
                    request = userKey.Legacy is { } legacy
                        ? _encryptor.Encrypt(cipher, legacy, capabilities)
                        : _encryptor.Encrypt(cipher, userKey.V2!, capabilities);
                }
            }
            finally
            {
                organizationKey?.Dispose();
            }
        }

        request = request.WithEncryptionContext(lease.AccountId);
        _session.ThrowIfExpired(lease);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        if (isCreate)
        {
            var wrappedKey = request.Key
                ?? throw new InvalidDataException("A new cipher request did not contain an item key.");
            var witness = new CipherCreateWitness(
                lease.AccountId,
                lease.Endpoints,
                cipher.Type,
                cipher.OrganizationId,
                cipher.CollectionIds,
                openingSnapshot.Select(candidate => candidate.Id).ToArray(),
                CipherCreateWitness.ComputeKeyFingerprint(wrappedKey));
            var pending = new CipherCreatePending(
                witness,
                CommitConfirmed: false,
                ExpectedCipherId: null);
            var reservation = ReserveCipherCreate(pending);
            try
            {
                var createdId = await CreateCipherAndReconcileAsync(
                    api,
                    cipher,
                    request,
                    lease,
                    pending,
                    reservation,
                    linked.Token);
                ConfirmAndClearCipherCreate(reservation, createdId);
                return createdId;
            }
            catch (VaultWriteException exception) when (IsDefinitiveClientRejection(exception))
            {
                AbandonCipherCreateAfterDefinitiveRejection(reservation);
                throw;
            }
            catch (CipherCreateOutcomeUncertainException exception)
            {
                RetainPendingCipherCreate(reservation, exception.Pending);
                throw;
            }
            catch (CipherCreateSnapshotMismatchException exception)
            {
                RetainPendingCipherCreate(reservation, exception.Pending);
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                RetainPendingCipherCreate(reservation, pending);
                throw;
            }
            catch (OutOfMemoryException)
            {
                RetainPendingCipherCreate(reservation, pending);
                throw;
            }
        }

        await api.UpdateCipherAsync(cipher.Id, request, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);
        return cipher.Id;
    }

    public async Task<CipherCreateReconciliationResult> SyncAndReconcileCipherCreateAsync(
        CipherCreatePending pending,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(pending.Witness);

        VaultSessionLease lease;
        try
        {
            lease = _session.CaptureLease();
        }
        catch (InvalidOperationException)
        {
            return SessionMismatch();
        }

        if (!WitnessMatchesLease(pending.Witness, lease))
            return SessionMismatch();

        var reconciliation = BeginCipherCreateReconciliation(pending, out var persisted);
        var result = await _sync.SyncAndReconcileCipherCreateAsync(lease, persisted, ct);
        if (result.Status == CipherCreateReconciliationStatus.Confirmed)
            ConfirmAndClearCipherCreate(reconciliation, result.ConfirmedId!);
        else
            RetainPendingCipherCreate(reconciliation, persisted);
        return result;
    }

    public CipherCreatePending? GetPendingCipherCreate()
    {
        var scope = CurrentCipherCreateScope();
        if (scope is null)
            return null;

        lock (_cipherCreateGate)
        {
            if (_cipherCreates.TryGetValue(scope, out var inMemory)
                && inMemory.InFlightReservation is not null)
            {
                return null;
            }

            var result = ReadPersistentCipherCreate(scope);
            if (result.Status == CipherCreatePendingReadStatus.Found)
            {
                var loaded = MergeLoadedCipherCreate(result.Record!, inMemory);
                _cipherCreates[scope] = loaded;
                return loaded.Record.IsInFlight ? null : loaded.Pending;
            }

            if (result.Status == CipherCreatePendingReadStatus.Absent)
            {
                _cipherCreates.Remove(scope);
                return null;
            }

            return inMemory?.Record.IsInFlight == false ? inMemory.Pending : null;
        }
    }

    public bool HasPendingOrInFlightCipherCreate()
    {
        var scope = CurrentCipherCreateScope();
        if (scope is null)
            return false;
        lock (_cipherCreateGate)
        {
            if (_cipherCreates.TryGetValue(scope, out var inMemory)
                && inMemory.InFlightReservation is not null)
            {
                return true;
            }

            var result = ReadPersistentCipherCreate(scope);
            if (result.Status == CipherCreatePendingReadStatus.Found)
            {
                _cipherCreates[scope] = MergeLoadedCipherCreate(result.Record!, inMemory);
                return true;
            }
            if (result.Status == CipherCreatePendingReadStatus.Absent)
            {
                _cipherCreates.Remove(scope);
                return false;
            }
            return true;
        }
    }

    public bool AbandonPendingCipherCreate(CipherCreatePending expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(expected.Witness);
        var currentScope = CurrentCipherCreateScope();
        var expectedReservation = CipherCreateReservation.From(expected);
        if (currentScope is null || currentScope != expectedReservation.Scope)
            return false;

        var removed = false;
        lock (_cipherCreateGate)
        {
            _cipherCreates.TryGetValue(currentScope, out var inMemory);
            if (inMemory?.InFlightReservation is not null)
            {
                return false;
            }

            var result = ReadPersistentCipherCreate(currentScope);
            if (result.Status == CipherCreatePendingReadStatus.Corrupt)
                throw new InvalidDataException("The pending-create ledger is corrupt.");
            if (result.Status == CipherCreatePendingReadStatus.Unavailable)
                throw new IOException("The pending-create ledger is unavailable.");
            if (result.Status != CipherCreatePendingReadStatus.Found)
                return false;
            var loaded = MergeLoadedCipherCreate(result.Record!, inMemory);
            _cipherCreates[currentScope] = loaded;
            if (loaded.Record.IsInFlight
                || !Matches(loaded.Pending, expectedReservation)
                || !PendingStateMatches(loaded.Pending, expected))
            {
                return false;
            }

            removed = _cipherCreateStore.Abandon(loaded.Record);
            if (removed)
                _cipherCreates.Remove(currentScope);
        }
        if (removed)
            RaiseCipherCreateGateChanged();
        return removed;
    }

    private async Task<string> CreateCipherAndReconcileAsync(
        IVaultWriteApiClient api,
        Cipher cipher,
        CipherRequest request,
        VaultSessionLease lease,
        CipherCreatePending pending,
        CipherCreateReservation reservation,
        CancellationToken mutationToken)
    {
        Exception? mutationException = null;
        try
        {
            CipherDto? created;
            if (!string.IsNullOrWhiteSpace(cipher.OrganizationId))
            {
                created = await api.CreateOrganizationCipherWithResponseAsync(
                    new ShareCipherRequest
                    {
                        Cipher = request,
                        CollectionIds = cipher.CollectionIds.ToArray(),
                    },
                    mutationToken);
            }
            else
            {
                created = await api.CreateCipherWithResponseAsync(request, mutationToken);
            }

            pending = pending with { CommitConfirmed = true };
            pending = pending with { ExpectedCipherId = GetAuthoritativeCreatedId(created) };
        }
        catch (CipherCreateSuccessResponseReadException exception)
        {
            pending = pending with { CommitConfirmed = true };
            mutationException = exception;
        }
        catch (VaultWriteException exception) when (IsDefinitiveClientRejection(exception))
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            mutationException = exception;
        }

        if (pending.CommitConfirmed)
        {
            try
            {
                pending = UpdateInFlightCipherCreate(reservation, pending);
            }
            catch (Exception persistenceException) when (persistenceException is not OutOfMemoryException)
            {
                throw new CipherCreateOutcomeUncertainException(
                    "The cipher was accepted, but its pending-create ledger could not be upgraded.",
                    pending,
                    mutationException,
                    persistenceException);
            }
        }

        return await SyncAndResolveCreatedCipherAsync(
            lease,
            pending,
            reservation,
            mutationException);
    }

    private async Task<string> SyncAndResolveCreatedCipherAsync(
        VaultSessionLease lease,
        CipherCreatePending pending,
        CipherCreateReservation reservation,
        Exception? mutationException)
    {
        CipherCreateReconciliationResult reconciliation;
        try
        {
            reconciliation = await _sync.SyncAndReconcileCipherCreateAsync(
                lease,
                pending,
                CancellationToken.None);
            _session.ThrowIfExpired(lease);
        }
        catch (Exception refreshException) when (refreshException is not OutOfMemoryException)
        {
            _session.TryMarkDataStale(lease);
            throw new CipherCreateOutcomeUncertainException(
                "The cipher may have been created, but the authoritative refresh failed.",
                pending,
                mutationException,
                refreshException);
        }
        catch (OutOfMemoryException)
        {
            _session.TryMarkDataStale(lease);
            throw;
        }

        if (!_session.IsCurrent(lease))
            reconciliation = SessionMismatch();

        if (reconciliation.Status == CipherCreateReconciliationStatus.Confirmed)
            return reconciliation.ConfirmedId!;

        if (reconciliation.Status == CipherCreateReconciliationStatus.SessionMismatch)
        {
            _session.TryMarkDataStale(lease);
            throw new CipherCreateOutcomeUncertainException(
                "The cipher may have been created, but its original session is no longer current.",
                pending,
                mutationException,
                new OperationCanceledException(
                    "The cipher create session changed during reconciliation.",
                    lease.CancellationToken));
        }

        if (!pending.CommitConfirmed
            && reconciliation.CandidateIds.Any(candidateId =>
                !pending.Witness.BaselineCipherIds.Contains(candidateId, StringComparer.Ordinal)))
        {
            pending = pending with { CommitConfirmed = true };
            try
            {
                pending = UpdateInFlightCipherCreate(reservation, pending);
            }
            catch (Exception persistenceException) when (persistenceException is not OutOfMemoryException)
            {
                throw new CipherCreateOutcomeUncertainException(
                    "The created cipher was observed, but its pending-create ledger could not be upgraded.",
                    pending,
                    mutationException,
                    persistenceException);
            }
        }

        throw new CipherCreateSnapshotMismatchException(
            "The authoritative snapshot could not uniquely confirm the created cipher.",
            pending,
            reconciliation);
    }

    private static bool IsDefinitiveClientRejection(VaultWriteException exception) =>
        exception.StatusCode is not null
        && (int)exception.StatusCode is >= 400 and <= 499;

    private static bool WitnessMatchesLease(
        CipherCreateWitness witness,
        VaultSessionLease lease) =>
        string.Equals(witness.AccountId, lease.AccountId, StringComparison.Ordinal)
        && witness.Endpoints == lease.Endpoints;

    private static CipherCreateReconciliationResult SessionMismatch() => new(
        CipherCreateReconciliationStatus.SessionMismatch,
        ConfirmedId: null,
        CandidateIds: Array.Empty<string>());

    private CipherCreateScope? CurrentCipherCreateScope()
    {
        try
        {
            return CipherCreateScope.From(_session.CaptureLease());
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private CipherCreateReservation ReserveCipherCreate(CipherCreatePending pending)
    {
        var reservation = CipherCreateReservation.From(pending);
        lock (_cipherCreateGate)
        {
            if (_cipherCreates.ContainsKey(reservation.Scope))
                throw new CipherCreatePendingOperationException();
            var inFlight = _cipherCreateStore.TryCreate(pending);
            if (inFlight is null)
                throw new CipherCreatePendingOperationException();
            try
            {
                _cipherCreates.Add(
                    reservation.Scope,
                    new CipherCreateGateEntry(
                        inFlight.Record,
                        inFlight.Record.Pending,
                        inFlight));
            }
            catch
            {
                inFlight.Dispose();
                throw;
            }
        }
        RaiseCipherCreateGateChanged();
        return reservation;
    }

    private CipherCreateReservation BeginCipherCreateReconciliation(
        CipherCreatePending pending,
        out CipherCreatePending persisted)
    {
        var reservation = CipherCreateReservation.From(pending);
        lock (_cipherCreateGate)
        {
            _cipherCreates.TryGetValue(reservation.Scope, out var inMemory);
            if (inMemory?.InFlightReservation is not null)
            {
                throw new CipherCreatePendingOperationException();
            }

            var result = ReadPersistentCipherCreate(reservation.Scope);
            if (result.Status != CipherCreatePendingReadStatus.Found)
                throw new CipherCreatePendingOperationException();
            var existing = MergeLoadedCipherCreate(result.Record!, inMemory);
            if (existing.Record.IsInFlight || !Matches(existing.Pending, reservation))
                throw new CipherCreatePendingOperationException();

            var updated = _cipherCreateStore.TryUpdate(existing.Record, pending)
                ?? throw new CipherCreatePendingOperationException();
            persisted = updated.Pending;
            _cipherCreates[reservation.Scope] = new CipherCreateGateEntry(
                updated,
                updated.Pending,
                InFlightReservation: null);
        }
        RaiseCipherCreateGateChanged();
        return reservation;
    }

    private CipherCreatePending UpdateInFlightCipherCreate(
        CipherCreateReservation reservation,
        CipherCreatePending pending)
    {
        lock (_cipherCreateGate)
        {
            if (!_cipherCreates.TryGetValue(reservation.Scope, out var existing)
                || existing.InFlightReservation is null
                || !Matches(existing.Pending, reservation))
            {
                throw new InvalidOperationException(
                    "The pending-create reservation changed before it could be upgraded.");
            }

            var persisted = _cipherCreateStore.TryUpdate(
                    existing.Record,
                    Merge(existing.Pending, pending))
                ?? throw new InvalidOperationException(
                    "The pending-create reservation is no longer available for upgrade.");
            _cipherCreates[reservation.Scope] = new CipherCreateGateEntry(
                persisted,
                persisted.Pending,
                existing.InFlightReservation);
            return persisted.Pending;
        }
    }

    private void RetainPendingCipherCreate(
        CipherCreateReservation reservation,
        CipherCreatePending pending)
    {
        var changed = false;
        lock (_cipherCreateGate)
        {
            if (_cipherCreates.TryGetValue(reservation.Scope, out var existing)
                && Matches(existing.Pending, reservation))
            {
                var merged = Merge(existing.Pending, pending);
                var record = existing.Record;
                try
                {
                    if (existing.InFlightReservation is not null)
                    {
                        record = _cipherCreateStore.Release(record, merged) ?? record;
                    }
                    else
                    {
                        record = _cipherCreateStore.TryUpdate(record, merged) ?? record;
                    }
                    merged = Merge(record.Pending, merged);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // The original durable reservation remains fail-closed. Keep the strongest
                    // process-local state so the current process cannot dispatch another create.
                }
                finally
                {
                    existing.InFlightReservation?.Dispose();
                }
                _cipherCreates[reservation.Scope] = new CipherCreateGateEntry(
                    record,
                    merged,
                    InFlightReservation: null);
                changed = true;
            }
        }
        if (changed)
            RaiseCipherCreateGateChanged();
    }

    private void ConfirmAndClearCipherCreate(
        CipherCreateReservation reservation,
        string confirmedId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedId);
        var changed = false;
        lock (_cipherCreateGate)
        {
            if (_cipherCreates.TryGetValue(reservation.Scope, out var existing)
                && Matches(existing.Pending, reservation))
            {
                var terminal = Merge(
                    existing.Pending,
                    existing.Pending with
                    {
                        CommitConfirmed = true,
                        ExpectedCipherId = confirmedId,
                    });
                _cipherCreates[reservation.Scope] = existing with { Pending = terminal };
                var updated = _cipherCreateStore.TryUpdate(existing.Record, terminal)
                    ?? throw new InvalidOperationException(
                        "The confirmed pending-create reservation changed before its final upgrade.");
                _cipherCreates[reservation.Scope] = existing with
                {
                    Record = updated,
                    Pending = updated.Pending,
                };
                if (!_cipherCreateStore.RemoveConfirmed(updated))
                {
                    throw new InvalidOperationException(
                        "The confirmed pending-create reservation changed before removal.");
                }
                changed = _cipherCreates.Remove(reservation.Scope);
                existing.InFlightReservation?.Dispose();
            }
        }
        if (changed)
            RaiseCipherCreateGateChanged();
    }

    private void AbandonCipherCreateAfterDefinitiveRejection(
        CipherCreateReservation reservation)
    {
        var changed = false;
        try
        {
            lock (_cipherCreateGate)
            {
                if (!_cipherCreates.TryGetValue(reservation.Scope, out var existing)
                    || existing.InFlightReservation is null
                    || !Matches(existing.Pending, reservation))
                {
                    throw new InvalidOperationException(
                        "The rejected pending-create reservation changed before removal.");
                }

                var released = _cipherCreateStore.Release(existing.Record, existing.Pending)
                    ?? throw new InvalidOperationException(
                        "The rejected pending-create reservation changed before release.");
                existing.InFlightReservation.Dispose();
                _cipherCreates[reservation.Scope] = new CipherCreateGateEntry(
                    released,
                    released.Pending,
                    InFlightReservation: null);
                if (!_cipherCreateStore.Abandon(released))
                {
                    throw new InvalidOperationException(
                        "The rejected pending-create reservation changed before removal.");
                }
                changed = _cipherCreates.Remove(reservation.Scope);
            }
        }
        catch
        {
            lock (_cipherCreateGate)
            {
                if (_cipherCreates.TryGetValue(reservation.Scope, out var existing)
                    && Matches(existing.Pending, reservation))
                {
                    existing.InFlightReservation?.Dispose();
                    _cipherCreates[reservation.Scope] = existing with
                    {
                        InFlightReservation = null,
                    };
                    changed = true;
                }
            }
            if (changed)
                RaiseCipherCreateGateChanged();
            throw;
        }

        if (changed)
            RaiseCipherCreateGateChanged();
    }

    private CipherCreatePendingReadResult ReadPersistentCipherCreate(
        CipherCreateScope scope)
    {
        try
        {
            return _cipherCreateStore.Read(scope.AccountId, scope.Endpoints);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CipherCreatePendingReadResult.Unavailable;
        }
    }

    private CipherCreateGateEntry MergeLoadedCipherCreate(
        CipherCreatePendingRecord persisted,
        CipherCreateGateEntry? inMemory)
    {
        if (inMemory is null
            || !string.Equals(
                inMemory.Pending.Witness.KeyFingerprint,
                persisted.Pending.Witness.KeyFingerprint,
                StringComparison.Ordinal))
        {
            return new CipherCreateGateEntry(
                persisted,
                persisted.Pending,
                InFlightReservation: null);
        }

        CipherCreatePending merged;
        try
        {
            merged = Merge(persisted.Pending, inMemory.Pending);
        }
        catch (InvalidDataException)
        {
            return new CipherCreateGateEntry(
                persisted,
                persisted.Pending,
                InFlightReservation: null);
        }

        if (persisted.IsInFlight
            || (merged.CommitConfirmed == persisted.Pending.CommitConfirmed
            && string.Equals(
                merged.ExpectedCipherId,
                persisted.Pending.ExpectedCipherId,
                StringComparison.Ordinal)))
        {
            return new CipherCreateGateEntry(
                persisted,
                persisted.Pending,
                InFlightReservation: null);
        }

        try
        {
            var updated = _cipherCreateStore.TryUpdate(persisted, merged);
            return updated is null
                ? new CipherCreateGateEntry(persisted, merged, null)
                : new CipherCreateGateEntry(updated, updated.Pending, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new CipherCreateGateEntry(persisted, merged, null);
        }
    }

    private void RaiseCipherCreateGateChanged()
    {
        var handlers = CipherCreateGateChanged;
        if (handlers is null)
            return;

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // UI observers cannot affect the safety state of a create operation.
            }
        }
    }

    private static bool Matches(
        CipherCreatePending pending,
        CipherCreateReservation reservation) =>
        string.Equals(
            pending.Witness.KeyFingerprint,
            reservation.KeyFingerprint,
            StringComparison.Ordinal);

    private static CipherCreatePending Merge(
        CipherCreatePending existing,
        CipherCreatePending incoming)
    {
        if (existing.ExpectedCipherId is not null
            && incoming.ExpectedCipherId is not null
            && !string.Equals(
                existing.ExpectedCipherId,
                incoming.ExpectedCipherId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The expected cipher identifier cannot be changed.");
        }

        return existing with
        {
            CommitConfirmed = existing.CommitConfirmed || incoming.CommitConfirmed,
            ExpectedCipherId = existing.ExpectedCipherId ?? incoming.ExpectedCipherId,
        };
    }

    private static bool PendingStateMatches(
        CipherCreatePending left,
        CipherCreatePending right) =>
        left.CommitConfirmed == right.CommitConfirmed
        && string.Equals(
            left.ExpectedCipherId,
            right.ExpectedCipherId,
            StringComparison.Ordinal);

    private sealed record CipherCreateScope(string AccountId, ServerEndpoints Endpoints)
    {
        public static CipherCreateScope From(VaultSessionLease lease) =>
            new(lease.AccountId, lease.Endpoints);

        public static CipherCreateScope From(CipherCreateWitness witness) =>
            new(witness.AccountId, witness.Endpoints);
    }

    private sealed record CipherCreateReservation(
        CipherCreateScope Scope,
        string KeyFingerprint)
    {
        public static CipherCreateReservation From(CipherCreatePending pending) =>
            new(
                CipherCreateScope.From(pending.Witness),
                pending.Witness.KeyFingerprint);
    }

    private sealed record CipherCreateGateEntry(
        CipherCreatePendingRecord Record,
        CipherCreatePending Pending,
        ICipherCreatePendingReservation? InFlightReservation);

    private sealed class UnavailableCipherCreatePendingStore : ICipherCreatePendingStore
    {
        public static UnavailableCipherCreatePendingStore Instance { get; } = new();

        private UnavailableCipherCreatePendingStore()
        {
        }

        public CipherCreatePendingReadResult Read(string accountId, ServerEndpoints endpoints) =>
            CipherCreatePendingReadResult.Unavailable;

        public ICipherCreatePendingReservation? TryCreate(CipherCreatePending pending) =>
            throw NotConfigured();

        public CipherCreatePendingRecord? TryUpdate(
            CipherCreatePendingRecord expected,
            CipherCreatePending pending) => throw NotConfigured();

        public CipherCreatePendingRecord? Release(
            CipherCreatePendingRecord expected,
            CipherCreatePending pending) => throw NotConfigured();

        public bool RemoveConfirmed(CipherCreatePendingRecord expected) => throw NotConfigured();

        public bool Abandon(CipherCreatePendingRecord expected) => throw NotConfigured();

        private static InvalidOperationException NotConfigured() => new(
            "A durable pending-create store must be configured before cipher creation.");
    }

    public async Task DeleteCipherAsync(string cipherId, bool permanent, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        RequireCipherPermissions(lease, [cipherId], cipher => cipher.CanDelete, "Cipher deletion is not permitted.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        if (permanent)
            await api.HardDeleteCipherAsync(cipherId, linked.Token);
        else
            await api.SoftDeleteCipherAsync(cipherId, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);
    }

    public async Task RestoreCipherAsync(string cipherId, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        RequireCipherPermissions(lease, [cipherId], cipher => cipher.CanRestore, "Cipher restoration is not permitted.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.RestoreCipherAsync(cipherId, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);
    }

    public async Task TransferCipherToOrganizationAsync(
        CipherPlacementBaseline baseline,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        var capabilities = _session.GetCapabilities(lease);
        var cipher = RequireMatchingPlacementBaseline(lease, baseline);
        ValidateTransferSource(cipher, capabilities);
        var selectedCollectionIds = ValidateWritableCollections(
            lease,
            organizationId,
            collectionIds,
            requireAtLeastOne: true,
            requireOrganizationKey: true);

        CipherRequest encrypted;
        if (!_session.TryCopyUserKeyMaterial(lease, out var copiedUserKey) || copiedUserKey is null)
            throw new OperationCanceledException(lease.CancellationToken);
        using (copiedUserKey)
        using (var organizationKey = ResolveOrganizationKey(lease, organizationId, copiedUserKey))
        {
            encrypted = copiedUserKey.Legacy is { } legacy
                ? _encryptor.EncryptForOrganizationTransfer(
                    cipher,
                    legacy,
                    organizationKey,
                    organizationId,
                    capabilities)
                : _encryptor.EncryptForOrganizationTransfer(
                    cipher,
                    copiedUserKey.V2!,
                    organizationKey,
                    organizationId,
                    capabilities);
        }
        encrypted = encrypted.WithEncryptionContext(lease.AccountId);

        await ExecutePotentiallyPartialMutationAsync(
            lease,
            (api, token) => api.ShareExistingCipherAsync(
                cipher.Id,
                new ShareCipherRequest
                {
                    Cipher = encrypted,
                    CollectionIds = selectedCollectionIds,
                },
                token),
            () => EnsurePlacementPostcondition(
                lease,
                [cipher.Id],
                organizationId,
                selectedCollectionIds,
                exactCollections: true),
            () => HasPlacementChanged(lease, [baseline]),
            ct);
    }

    public async Task SetCipherCollectionsAsync(
        CipherPlacementBaseline baseline,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        var cipher = RequireMatchingPlacementBaseline(lease, baseline);
        var organizationId = cipher.OrganizationId
            ?? throw new OrganizationCipherOperationException(
                "A personal cipher does not have organization collections.");
        ValidateCollectionAssignmentPermission(cipher);
        var replacement = ValidateExactCollectionReplacement(
            lease,
            cipher,
            collectionIds);

        await ExecutePotentiallyPartialMutationAsync(
            lease,
            (api, token) => api.SetCipherCollectionsAsync(
                cipher.Id,
                new CipherCollectionsRequest { CollectionIds = replacement.WritableCollectionIds },
                token),
            () => EnsurePlacementPostcondition(
                lease,
                [cipher.Id],
                organizationId,
                replacement.ExpectedCollectionIds,
                exactCollections: true),
            () => HasPlacementChanged(lease, [baseline]),
            ct);
    }

    public async Task TransferCiphersToOrganizationAsync(
        IReadOnlyCollection<CipherPlacementBaseline> baselines,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baselines);
        if (baselines.Count == 0)
            return;
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        var capabilities = _session.GetCapabilities(lease);
        var ciphers = RequireMatchingPlacementBaselines(lease, baselines);
        foreach (var cipher in ciphers)
            ValidateTransferSource(cipher, capabilities);
        var selectedCollectionIds = ValidateWritableCollections(
            lease,
            organizationId,
            collectionIds,
            requireAtLeastOne: true,
            requireOrganizationKey: true);

        CipherWithIdRequest[] encrypted;
        if (!_session.TryCopyUserKeyMaterial(lease, out var copiedUserKey) || copiedUserKey is null)
            throw new OperationCanceledException(lease.CancellationToken);
        using (copiedUserKey)
        using (var organizationKey = ResolveOrganizationKey(lease, organizationId, copiedUserKey))
        {
            encrypted = ciphers.Select(cipher => new CipherWithIdRequest(
                cipher.Id,
                copiedUserKey.Legacy is { } legacy
                    ? _encryptor.EncryptForOrganizationTransfer(
                        cipher,
                        legacy,
                        organizationKey,
                        organizationId,
                        capabilities)
                    : _encryptor.EncryptForOrganizationTransfer(
                        cipher,
                        copiedUserKey.V2!,
                        organizationKey,
                        organizationId,
                        capabilities))).ToArray();
        }
        encrypted = encrypted.Select(request => new CipherWithIdRequest(
            request.Id,
            request.WithEncryptionContext(lease.AccountId))).ToArray();

        var cipherIds = ciphers.Select(cipher => cipher.Id).ToArray();
        await ExecutePotentiallyPartialMutationAsync(
            lease,
            (api, token) => api.BulkShareCiphersAsync(
                new BulkShareCiphersRequest
                {
                    Ciphers = encrypted,
                    CollectionIds = selectedCollectionIds,
                },
                token),
            () => EnsurePlacementPostcondition(
                lease,
                cipherIds,
                organizationId,
                selectedCollectionIds,
                exactCollections: true),
            () => HasPlacementChanged(lease, baselines),
            ct);
    }

    public async Task BulkUpdateCipherCollectionsAsync(
        IReadOnlyCollection<CipherPlacementBaseline> baselines,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        bool removeCollections,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baselines);
        if (baselines.Count == 0 || collectionIds.Count == 0)
            return;
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        var capabilities = _session.GetCapabilities(lease);
        var ciphers = RequireMatchingPlacementBaselines(lease, baselines);
        if (ciphers.Any(cipher =>
                !string.Equals(cipher.OrganizationId, organizationId, StringComparison.Ordinal)))
        {
            throw new OrganizationCipherOperationException(
                "Bulk collection updates require ciphers from one organization.");
        }
        foreach (var cipher in ciphers)
        {
            ValidateCollectionAssignmentPermission(cipher);
            CipherWritePolicy.EnsureCanWrite(cipher, capabilities);
        }
        var changedCollectionIds = ValidateWritableCollections(
            lease,
            organizationId,
            collectionIds,
            requireAtLeastOne: true,
            requireOrganizationKey: false);

        var cipherIds = ciphers.Select(cipher => cipher.Id).ToArray();
        await ExecutePotentiallyPartialMutationAsync(
            lease,
            (api, token) => api.BulkUpdateCipherCollectionsAsync(
                new BulkCipherCollectionsRequest
                {
                    OrganizationId = organizationId,
                    CipherIds = cipherIds,
                    CollectionIds = changedCollectionIds,
                    RemoveCollections = removeCollections,
                },
                token),
            () => EnsurePlacementPostcondition(
                lease,
                cipherIds,
                organizationId,
                changedCollectionIds,
                exactCollections: false,
                removeCollections),
            () => HasPlacementChanged(lease, baselines),
            ct);
    }

    public Task ArchiveCipherAsync(string cipherId, CancellationToken ct = default) =>
        SetCipherArchivedAsync(cipherId, archived: true, ct);

    public Task UnarchiveCipherAsync(string cipherId, CancellationToken ct = default) =>
        SetCipherArchivedAsync(cipherId, archived: false, ct);

    public async Task SetCipherFavoriteAsync(
        string cipherId,
        bool favorite,
        CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        var cipher = _session.GetCiphers(lease)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, cipherId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Cipher is not present in the current vault snapshot.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.UpdateCipherPartialAsync(
            cipherId,
            new PartialCipherRequest
            {
                FolderId = cipher.FolderId,
                Favorite = favorite,
            },
            linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);
    }

    private async Task SetCipherArchivedAsync(string cipherId, bool archived, CancellationToken ct)
    {
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        EnsureArchiveOperationSupported(lease, archived);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        if (archived)
            await api.ArchiveCipherAsync(cipherId, linked.Token);
        else
            await api.UnarchiveCipherAsync(cipherId, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);
    }

    public async Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        if (!_session.TryCopyUserKeyMaterial(lease, out var copiedKey) || copiedKey is null)
            throw new InvalidOperationException("Vault is locked.");
        FolderRequest request;
        using (copiedKey)
        {
            request = new FolderRequest
            {
                Name = copiedKey.Legacy is { } legacy
                    ? _encryptor.EncryptFolderName(name, legacy)
                    : _encryptor.EncryptFolderName(name, copiedKey.V2!),
            };
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        _session.ThrowIfExpired(lease);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        if (string.IsNullOrEmpty(folderId))
            await api.CreateFolderAsync(request, linked.Token);
        else
            await api.UpdateFolderAsync(folderId, request, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);
    }

    public async Task DeleteFolderAsync(string folderId, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.DeleteFolderAsync(folderId, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);
    }

    public async Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        if (ids.Count == 0) return;
        await ExecutePotentiallyPartialMutationAsync(
            lease,
            (api, token) => api.BulkMoveCiphersAsync(
                ids,
                string.IsNullOrWhiteSpace(folderId) ? null : folderId,
                token),
            ct);
    }

    public async Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        if (ids.Count == 0) return;
        RequireCipherPermissions(lease, ids, cipher => cipher.CanDelete, "Cipher deletion is not permitted.");
        await ExecutePotentiallyPartialMutationAsync(
            lease,
            (api, token) => permanent
                ? api.BulkHardDeleteCiphersAsync(ids, token)
                : api.BulkSoftDeleteCiphersAsync(ids, token),
            ct);
    }

    public async Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        if (ids.Count == 0) return;
        RequireCipherPermissions(lease, ids, cipher => cipher.CanRestore, "Cipher restoration is not permitted.");
        await ExecutePotentiallyPartialMutationAsync(
            lease,
            (api, token) => api.BulkRestoreCiphersAsync(ids, token),
            ct);
    }

    public Task ArchiveCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
        SetCiphersArchivedAsync(ids, archived: true, ct);

    public Task UnarchiveCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
        SetCiphersArchivedAsync(ids, archived: false, ct);

    private async Task SetCiphersArchivedAsync(
        IReadOnlyCollection<string> ids,
        bool archived,
        CancellationToken ct)
    {
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        if (ids.Count == 0)
            return;
        EnsureArchiveOperationSupported(lease, archived);
        await ExecutePotentiallyPartialMutationAsync(
            lease,
            (api, token) => archived
                ? api.BulkArchiveCiphersAsync(ids, token)
                : api.BulkUnarchiveCiphersAsync(ids, token),
            ct);
    }

    private void RequireUnlocked(VaultSessionLease lease)
    {
        if (!_session.IsUnlocked(lease))
            throw new InvalidOperationException("Vault is locked.");
    }

    private static void ValidateOwnershipAndWriteAccess(Cipher cipher, Cipher? current)
    {
        if (current is null)
        {
            if (!string.IsNullOrEmpty(cipher.Id))
            {
                if (!string.IsNullOrWhiteSpace(cipher.OrganizationId))
                {
                    throw new OrganizationCipherOperationException(
                        "The organization cipher is not present in the current vault snapshot.");
                }
                throw new CipherEditConflictException(
                    "The cipher is not present in the current vault snapshot. Reload before saving.");
            }
            return;
        }

        var currentOrganizationId = string.IsNullOrWhiteSpace(current.OrganizationId)
            ? null
            : current.OrganizationId;
        var requestedOrganizationId = string.IsNullOrWhiteSpace(cipher.OrganizationId)
            ? null
            : cipher.OrganizationId;
        if (current.Type != cipher.Type)
        {
            throw new CipherEditConflictException(
                "The cipher type cannot be changed by an update.");
        }
        if (current.RevisionDate != cipher.RevisionDate)
        {
            throw new CipherEditConflictException(
                "The cipher changed after the editor was opened. Reload it before saving.");
        }
        if (!current.CanViewPassword)
        {
            throw new OrganizationCipherOperationException(
                "A cipher with hidden password access cannot be overwritten.");
        }
        if (!string.Equals(currentOrganizationId, requestedOrganizationId, StringComparison.Ordinal))
            throw new OrganizationCipherOperationException("Cipher ownership cannot be changed by an update.");
        if (!string.Equals(current.Key, cipher.Key, StringComparison.Ordinal))
            throw new OrganizationCipherOperationException("The existing cipher key must be preserved.");

        if (currentOrganizationId is null)
            return;
        if (!current.CanEdit)
            throw new OrganizationCipherOperationException("The organization cipher is read-only.");
        if (!current.CollectionIds.ToHashSet(StringComparer.Ordinal)
            .SetEquals(cipher.CollectionIds))
        {
            throw new OrganizationCipherOperationException(
                "Collection membership must be changed through the collection endpoint.");
        }
    }

    private void ValidateNewOrganizationAssignment(Cipher cipher, VaultSessionLease lease)
    {
        var organizationId = cipher.OrganizationId!;
        var organization = _session.Organizations.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, organizationId, StringComparison.Ordinal));
        if (organization is not { IsMember: true, Enabled: true, Status: 2 }
            || string.IsNullOrWhiteSpace(organization.EncryptedKey))
        {
            throw new OrganizationCipherOperationException(
                "The target organization is not a confirmed writable membership.");
        }
        var selectedIds = cipher.CollectionIds.ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
            throw new OrganizationCipherOperationException(
                "Creating an organization cipher requires at least one writable collection assignment.");
        if (selectedIds.Count != cipher.CollectionIds.Count)
        {
            throw new OrganizationCipherOperationException(
                "Creating an organization cipher requires a unique collection assignment.");
        }

        var selectedCollections = _session.Collections
            .Where(collection => selectedIds.Contains(collection.Id))
            .ToArray();
        if (selectedCollections.Length != selectedIds.Count
            || selectedCollections.Any(collection =>
                collection.ReadOnly
                || !string.Equals(collection.OrganizationId, organizationId, StringComparison.Ordinal)))
        {
            throw new OrganizationCipherOperationException(
                "The selected organization collection is not writable.");
        }
        _session.ThrowIfExpired(lease);
    }

    private static string? GetAuthoritativeCreatedId(CipherDto? created)
    {
        if (created is null)
            return null;
        if (string.IsNullOrWhiteSpace(created.Id))
            throw new InvalidDataException("The create-cipher response did not contain an id.");
        return created.Id;
    }

    private Cipher RequireMatchingPlacementBaseline(
        VaultSessionLease lease,
        CipherPlacementBaseline baseline)
    {
        var current = _session.GetCiphers(lease).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, baseline.CipherId, StringComparison.Ordinal));
        if (current is null || !baseline.Matches(current))
        {
            throw new CipherEditConflictException(
                "The cipher placement changed. Reload before applying the operation.");
        }
        return current;
    }

    private Cipher[] RequireMatchingPlacementBaselines(
        VaultSessionLease lease,
        IReadOnlyCollection<CipherPlacementBaseline> baselines)
    {
        var ids = baselines.Select(baseline => baseline.CipherId).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ArgumentException("Cipher placement baselines must have unique IDs.", nameof(baselines));
        return baselines.Select(baseline => RequireMatchingPlacementBaseline(lease, baseline)).ToArray();
    }

    private static void ValidateTransferSource(
        Cipher cipher,
        VaultCapabilities capabilities)
    {
        if (!string.IsNullOrWhiteSpace(cipher.OrganizationId))
            throw new OrganizationCipherOperationException("Only personal ciphers can be transferred.");
        if (!cipher.CanEdit || !cipher.CanViewPassword)
            throw new OrganizationCipherOperationException("The cipher cannot be transferred.");
        CipherWritePolicy.EnsureCanWrite(cipher, capabilities);
    }

    private void EnsureArchiveOperationSupported(VaultSessionLease lease, bool archived)
    {
        var capabilities = _session.GetCapabilities(lease);
        if (archived ? !capabilities.CanArchive : !capabilities.CanUnarchive)
            throw new UnsupportedCipherArchiveOperationException(archived);
    }

    private string[] ValidateWritableCollections(
        VaultSessionLease lease,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        bool requireAtLeastOne,
        bool requireOrganizationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentNullException.ThrowIfNull(collectionIds);
        var selected = collectionIds.ToArray();
        if (requireAtLeastOne && selected.Length == 0)
            throw new OrganizationCipherOperationException("At least one writable collection is required.");
        var selectedSet = selected.ToHashSet(StringComparer.Ordinal);
        if (selectedSet.Count != selected.Length)
            throw new OrganizationCipherOperationException("Collection assignments must be unique.");

        var organization = _session.Organizations.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, organizationId, StringComparison.Ordinal));
        if (organization is not { IsMember: true, Enabled: true, Status: 2 }
            || (requireOrganizationKey && string.IsNullOrWhiteSpace(organization.EncryptedKey)))
        {
            throw new OrganizationCipherOperationException(
                "The target organization is unavailable in the current vault snapshot.");
        }

        var collections = _session.Collections
            .Where(collection => selectedSet.Contains(collection.Id))
            .ToArray();
        if (collections.Length != selectedSet.Count
            || collections.Any(collection =>
                collection.ReadOnly
                || !string.Equals(collection.OrganizationId, organizationId, StringComparison.Ordinal)))
        {
            throw new OrganizationCipherOperationException(
                "One or more target collections are unavailable or read-only.");
        }
        _session.ThrowIfExpired(lease);
        return selected;
    }

    private ExactCollectionReplacement ValidateExactCollectionReplacement(
        VaultSessionLease lease,
        Cipher cipher,
        IReadOnlyCollection<string> collectionIds)
    {
        ArgumentNullException.ThrowIfNull(collectionIds);
        var desired = collectionIds.ToArray();
        var desiredSet = desired.ToHashSet(StringComparer.Ordinal);
        if (desiredSet.Count != desired.Length)
            throw new OrganizationCipherOperationException("Collection assignments must be unique.");

        var currentSet = cipher.CollectionIds.ToHashSet(StringComparer.Ordinal);
        foreach (var collectionId in desiredSet.Except(currentSet).Concat(currentSet.Except(desiredSet)))
        {
            var collection = _session.Collections.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, collectionId, StringComparison.Ordinal));
            if (collection is null
                || collection.ReadOnly
                || !string.Equals(collection.OrganizationId, cipher.OrganizationId, StringComparison.Ordinal))
            {
                throw new OrganizationCipherOperationException(
                    "A read-only or unavailable collection assignment cannot be changed.");
            }
        }

        var writable = desired
            .Where(collectionId => _session.Collections.Any(collection =>
                string.Equals(collection.Id, collectionId, StringComparison.Ordinal)
                && string.Equals(collection.OrganizationId, cipher.OrganizationId, StringComparison.Ordinal)
                && !collection.ReadOnly))
            .ToArray();
        _session.ThrowIfExpired(lease);
        return new ExactCollectionReplacement(desired, writable);
    }

    private sealed record ExactCollectionReplacement(
        string[] ExpectedCollectionIds,
        string[] WritableCollectionIds);

    private void ValidateCollectionAssignmentPermission(Cipher cipher)
    {
        var assignedManageAccess = _session.Collections.Any(collection =>
            collection.Manage
            && cipher.CollectionIds.Contains(collection.Id, StringComparer.Ordinal));
        if (!(cipher.CanEdit && cipher.CanViewPassword) && !assignedManageAccess)
        {
            throw new OrganizationCipherOperationException(
                "Collection assignment is not permitted for this cipher.");
        }
    }

    private SymmetricCryptoKey ResolveOrganizationKey(
        VaultSessionLease lease,
        string organizationId,
        VaultUserKey userKey)
    {
        var organization = _session.Organizations.First(candidate =>
            string.Equals(candidate.Id, organizationId, StringComparison.Ordinal));
        if (!organization.IsMember)
        {
            throw new OrganizationCipherOperationException(
                "Provider-only organizations are not writable vault destinations.");
        }
        _session.ThrowIfExpired(lease);
        if (!_session.TryGetEncryptedPrivateKey(lease, out var encryptedPrivateKey))
            throw new OperationCanceledException(lease.CancellationToken);
        return userKey.Legacy is { } legacy
            ? _organizationKeys.Resolve(organization, encryptedPrivateKey, legacy)
            : _organizationKeys.Resolve(organization, encryptedPrivateKey, userKey.V2!);
    }

    private void EnsurePlacementPostcondition(
        VaultSessionLease lease,
        IReadOnlyCollection<string> cipherIds,
        string organizationId,
        IReadOnlyCollection<string> collectionIds,
        bool exactCollections,
        bool removeCollections = false)
    {
        var expectedCollections = collectionIds.ToHashSet(StringComparer.Ordinal);
        var unresolved = new List<string>();
        foreach (var cipherId in cipherIds)
        {
            var current = _session.GetCiphers(lease).FirstOrDefault(candidate =>
                string.Equals(candidate.Id, cipherId, StringComparison.Ordinal));
            if (current is null
                || !string.Equals(current.OrganizationId, organizationId, StringComparison.Ordinal))
            {
                unresolved.Add(cipherId);
                continue;
            }

            var actual = current.CollectionIds.ToHashSet(StringComparer.Ordinal);
            var satisfied = exactCollections
                ? actual.SetEquals(expectedCollections)
                : removeCollections
                    ? !actual.Overlaps(expectedCollections)
                    : actual.IsSupersetOf(expectedCollections);
            if (!satisfied)
                unresolved.Add(cipherId);
        }

        if (unresolved.Count > 0)
        {
            throw new VaultMutationReconciliationException(
                "The server did not apply the placement change to every cipher.",
                unresolved);
        }
    }

    private bool HasPlacementChanged(
        VaultSessionLease lease,
        IReadOnlyCollection<CipherPlacementBaseline> baselines) =>
        baselines.Any(baseline =>
        {
            var current = _session.GetCiphers(lease).FirstOrDefault(candidate =>
                string.Equals(candidate.Id, baseline.CipherId, StringComparison.Ordinal));
            return current is null || !baseline.Matches(current);
        });

    private void RequireCipherPermissions(
        VaultSessionLease lease,
        IReadOnlyCollection<string> ids,
        Func<Cipher, bool> isAllowed,
        string failureMessage)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        if (_session.GetCiphers(lease).Any(cipher =>
                selected.Contains(cipher.Id) && !isAllowed(cipher)))
        {
            throw new OrganizationCipherOperationException(failureMessage);
        }
    }

    private async Task ExecutePotentiallyPartialMutationAsync(
        VaultSessionLease lease,
        Func<IVaultWriteApiClient, CancellationToken, Task> mutation,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        try
        {
            await mutation(api, linked.Token);
        }
        catch (Exception mutationException) when (mutationException is not OutOfMemoryException)
        {
            await ReconcilePotentialPartialBatchCommitAsync(lease, mutationException);
            throw;
        }

        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, CancellationToken.None);
    }

    private async Task ExecutePotentiallyPartialMutationAsync(
        VaultSessionLease lease,
        Func<IVaultWriteApiClient, CancellationToken, Task> mutation,
        Action ensurePostcondition,
        Func<bool> hasMutationObserved,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        Exception? failure = null;
        try
        {
            await mutation(api, linked.Token);
        }
        catch (Exception mutationException) when (mutationException is not OutOfMemoryException)
        {
            await ReconcilePotentialPartialBatchCommitAsync(lease, mutationException);
            if (!_session.IsCurrent(lease))
                ExceptionDispatchInfo.Capture(mutationException).Throw();
            failure = mutationException;
        }

        if (failure is null)
        {
            _session.ThrowIfExpired(lease);
            await ResyncAsync(lease, CancellationToken.None);
        }

        try
        {
            ensurePostcondition();
        }
        catch (VaultMutationReconciliationException)
            when (failure is not null && !hasMutationObserved())
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task ReconcilePotentialPartialBatchCommitAsync(
        VaultSessionLease lease,
        Exception mutationException)
    {
        if (!_session.IsCurrent(lease))
            return;

        try
        {
            await _sync.SyncAsync(lease, CancellationToken.None);
        }
        catch (Exception refreshException) when (refreshException is not OutOfMemoryException)
        {
            if (!_session.TryMarkDataStale(lease))
                return;
            throw new VaultBatchMutationRecoveryException(
                "A bulk mutation may have partially committed, and the authoritative refresh failed.",
                mutationException,
                refreshException);
        }
        catch (OutOfMemoryException)
        {
            _session.TryMarkDataStale(lease);
            throw;
        }
    }

    // 写后整库重同步:写已在服务端成功,若此处刷新失败,变更仍已持久化,本地快照可能过期。
    // 包装为 VaultRefreshException 让上层区分"写失败"(重试)与"写成功但刷新失败"(手动刷新)。
    // 以请求原始 lease 同步,确保不会把结果落到已切换的账户上。
    private async Task ResyncAsync(VaultSessionLease lease, CancellationToken ct)
    {
        try
        {
            await _sync.SyncAsync(lease, ct);
        }
        catch (OperationCanceledException)
        {
            _session.TryMarkDataStale(lease);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _session.TryMarkDataStale(lease);
            throw new VaultRefreshException("更改已保存,但刷新本地数据失败,请手动刷新。", ex);
        }
    }
}
