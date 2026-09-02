using System.Net;
using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;

namespace Vault;

public sealed class AccountDeletionService : IAccountDeletionService
{
    private static readonly TimeSpan DefaultCoordinationWaitTimeout =
        TimeSpan.FromSeconds(30);
    private readonly CryptoService _crypto;
    private readonly IVaultApiClientProvider _clients;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;
    private readonly ICacheStore _cacheStore;
    private readonly IDeviceKeyStore _deviceKeyStore;
    private readonly ICipherCreatePendingStore _cipherCreateStore;
    private readonly ITrustedDeviceApprovalStore _trustedDeviceApprovalStore;
    private readonly ITdeV2JitPendingStore _tdeV2JitPendingStore;
    private readonly IFaviconCache _faviconCache;
    private readonly IAccountDeletionTombstoneStore _tombstones;
    private readonly IAccountDeletionCoordinationGate _coordinationGate;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _coordinationWaitTimeout;

    public AccountDeletionService(
        CryptoService crypto,
        IVaultApiClientProvider clients,
        VaultSession session,
        ITokenStore tokenStore,
        ICacheStore cacheStore,
        IDeviceKeyStore deviceKeyStore,
        ICipherCreatePendingStore cipherCreateStore,
        ITrustedDeviceApprovalStore trustedDeviceApprovalStore,
        ITdeV2JitPendingStore tdeV2JitPendingStore,
        IFaviconCache faviconCache,
        IAccountDeletionTombstoneStore tombstones,
        TimeProvider? timeProvider = null,
        IAccountDeletionCoordinationGate? coordinationGate = null,
        TimeSpan? coordinationWaitTimeout = null)
    {
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
        _deviceKeyStore = deviceKeyStore ?? throw new ArgumentNullException(nameof(deviceKeyStore));
        _cipherCreateStore = cipherCreateStore ?? throw new ArgumentNullException(nameof(cipherCreateStore));
        _trustedDeviceApprovalStore = trustedDeviceApprovalStore
            ?? throw new ArgumentNullException(nameof(trustedDeviceApprovalStore));
        _tdeV2JitPendingStore = tdeV2JitPendingStore
            ?? throw new ArgumentNullException(nameof(tdeV2JitPendingStore));
        _faviconCache = faviconCache ?? throw new ArgumentNullException(nameof(faviconCache));
        _tombstones = tombstones ?? throw new ArgumentNullException(nameof(tombstones));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _coordinationGate = coordinationGate
            ?? NoopAccountDeletionCoordinationGate.Instance;
        _coordinationWaitTimeout = coordinationWaitTimeout
            ?? DefaultCoordinationWaitTimeout;
        if (_coordinationWaitTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(coordinationWaitTimeout));
    }

    public AccountDeletionPreflight GetPreflight()
    {
        if (TryCaptureContext(out var context, out var rejected))
            return context.Preflight;

        throw new InvalidOperationException(
            rejected.Message ?? $"Account deletion preflight failed: {rejected.Reason}.");
    }

    public AccountDeletionTombstoneReadResult ReadPending(
        AccountDeletionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return _tombstones.Read(binding);
    }

    public AccountDeletionTombstoneReadAllResult ReadPending() =>
        _tombstones.ReadAll();

    public async Task<AccountDeletionOtpRequestResult> RequestOtpAsync(
        CancellationToken ct = default)
    {
        if (!TryCaptureContext(out var context, out var rejected))
            return ToOtpRejected(rejected);

        var preflight = context.Preflight;
        if (preflight.BlockReason != AccountDeletionPreflightBlockReason.None)
            return ToOtpRejected(RejectPreflight(preflight));
        if (preflight.CredentialRequirement
            != AccountDeletionCredentialRequirement.OneTimePassword)
        {
            return new AccountDeletionOtpRequestResult.Rejected(
                AccountDeletionRejectionReason.WrongCredentialKind,
                preflight);
        }

        var pending = ReadPendingForDispatch(preflight.Binding, preflight);
        if (pending is not null)
            return ToOtpRejected(pending);

        IAccountApiClient api;
        try
        {
            ct.ThrowIfCancellationRequested();
            api = _clients.GetAuthenticated<IAccountApiClient>(context.Lease);
            _session.ThrowIfExpired(context.Lease);
        }
        catch (OperationCanceledException)
        {
            return new AccountDeletionOtpRequestResult.CancelledBeforeDispatch(preflight);
        }
        catch (Exception exception)
        {
            return new AccountDeletionOtpRequestResult.Rejected(
                AccountDeletionRejectionReason.SessionBindingMismatch,
                preflight,
                Message: SafeMessage(exception));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            context.Lease.CancellationToken);
        var dispatched = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            _session.ThrowIfExpired(context.Lease);
            var request = api.RequestAccountDeletionOtpAsync(linked.Token);
            dispatched = true;
            await request.ConfigureAwait(false);
            _session.ThrowIfExpired(context.Lease);
            return new AccountDeletionOtpRequestResult.Sent(preflight);
        }
        catch (OperationCanceledException) when (!dispatched)
        {
            return new AccountDeletionOtpRequestResult.CancelledBeforeDispatch(preflight);
        }
        catch (HttpRequestException exception) when (IsDefiniteRejection(exception.StatusCode))
        {
            return new AccountDeletionOtpRequestResult.Rejected(
                AccountDeletionRejectionReason.ServerRejected,
                preflight,
                HttpStatusCode: ToStatusCode(exception.StatusCode),
                Message: ServerMessage(exception));
        }
        catch (Exception exception)
        {
            return new AccountDeletionOtpRequestResult.OutcomeUncertain(
                preflight,
                HttpStatusCode: ToStatusCode((exception as HttpRequestException)?.StatusCode),
                Message: SafeMessage(exception));
        }
    }

    public async Task<AccountDeletionResult> DeleteAsync(
        AccountDeletionCredential credential,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using (credential)
        {
            if (!TryCaptureContext(out var context, out var rejected))
                return rejected;

            var preflight = context.Preflight;
            if (preflight.BlockReason != AccountDeletionPreflightBlockReason.None)
                return RejectPreflight(preflight);

            var pendingResult = ReadPendingForDispatch(preflight.Binding, preflight);
            if (pendingResult is AccountDeletionResult.OutcomeUncertain uncertain)
            {
                TryQuarantineMatchingSession(preflight.Binding);
                return uncertain;
            }
            if (pendingResult is AccountDeletionResult.ConfirmedLocalCleanupIncomplete confirmed)
                return await ResumeConfirmedCleanupAsync(confirmed.Pending, ct).ConfigureAwait(false);
            if (pendingResult is AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete
                uncertainCleanup)
            {
                return await ResumeConfirmedCleanupAsync(
                    uncertainCleanup.Pending,
                    ct).ConfigureAwait(false);
            }
            if (pendingResult is not null)
                return pendingResult;

            var expectedKind = preflight.CredentialRequirement switch
            {
                AccountDeletionCredentialRequirement.MasterPassword =>
                    AccountDeletionCredentialKind.MasterPassword,
                AccountDeletionCredentialRequirement.OneTimePassword =>
                    AccountDeletionCredentialKind.OneTimePassword,
                _ => throw new InvalidOperationException(
                    "A fresh online authentication preflight cannot dispatch deletion."),
            };
            if (credential.Kind != expectedKind)
            {
                return new AccountDeletionResult.Rejected(
                    AccountDeletionRejectionReason.WrongCredentialKind,
                    preflight);
            }

            IAccountApiClient api;
            try
            {
                api = _clients.GetAuthenticated<IAccountApiClient>(context.Lease);
                _session.ThrowIfExpired(context.Lease);
            }
            catch (OperationCanceledException)
            {
                return new AccountDeletionResult.CancelledBeforeDispatch(preflight, Pending: null);
            }
            catch (Exception exception)
            {
                return new AccountDeletionResult.Rejected(
                    AccountDeletionRejectionReason.SessionBindingMismatch,
                    preflight,
                    Message: SafeMessage(exception));
            }

            return await DispatchDeleteAsync(
                context,
                preflight,
                api,
                credential,
                ct).ConfigureAwait(false);
        }
    }

    private async Task<AccountDeletionResult> DispatchDeleteAsync(
        DeletionContext context,
        AccountDeletionPreflight preflight,
        IAccountApiClient api,
        AccountDeletionCredential credential,
        CancellationToken ct)
    {
        IAccountDeletionCoordinationLease coordination;
        using var coordinationWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        coordinationWait.CancelAfter(_coordinationWaitTimeout);
        try
        {
            coordination = await _coordinationGate
                .AcquireAsync(coordinationWait.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new AccountDeletionResult.CancelledBeforeDispatch(preflight, Pending: null);
        }
        catch (Exception exception)
        {
            return new AccountDeletionResult.Rejected(
                AccountDeletionRejectionReason.LocalPersistenceUnavailable,
                preflight,
                Message: SafeMessage(exception));
        }
        await using var coordinationScope = coordination;

        var gatedPending = ReadPendingForDispatch(preflight.Binding, preflight);
        if (gatedPending is AccountDeletionResult.OutcomeUncertain gatedUncertain)
        {
            TryQuarantineMatchingSession(preflight.Binding);
            return gatedUncertain;
        }
        if (gatedPending is AccountDeletionResult.ConfirmedLocalCleanupIncomplete gatedTerminal)
            return CompleteConfirmedCleanup(gatedTerminal.Pending, preflight);
        if (gatedPending is AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete
            gatedUncertainTerminal)
        {
            return CompleteConfirmedCleanup(gatedUncertainTerminal.Pending, preflight);
        }
        if (gatedPending is not null)
            return gatedPending;
        if (!_session.IsUnlocked(context.Lease)
            || !_session.IsCurrent(context.Lease, context.Persisted))
        {
            return new AccountDeletionResult.CancelledBeforeDispatch(preflight, Pending: null);
        }

        DeleteAccountRequest request;
        try
        {
            ct.ThrowIfCancellationRequested();
            request = CreateRequest(credential, context.Persisted);
        }
        catch (OperationCanceledException)
        {
            return new AccountDeletionResult.CancelledBeforeDispatch(preflight, Pending: null);
        }
        catch (Exception exception) when (exception is
            ArgumentException or CryptographicException or FormatException)
        {
            return new AccountDeletionResult.Rejected(
                AccountDeletionRejectionReason.InvalidCredential,
                preflight);
        }

        AccountDeletionTombstoneRecord? prepared;
        try
        {
            prepared = _tombstones.TryCreate(
                preflight.Binding,
                _timeProvider.GetUtcNow());
        }
        catch (Exception exception)
        {
            return new AccountDeletionResult.Rejected(
                AccountDeletionRejectionReason.LocalPersistenceUnavailable,
                preflight,
                Message: SafeMessage(exception));
        }

        if (prepared is null)
        {
            var concurrent = ReadPendingForDispatch(preflight.Binding, preflight)
                ?? new AccountDeletionResult.Rejected(
                    AccountDeletionRejectionReason.ConcurrentStateChange,
                    preflight);
            if (concurrent is AccountDeletionResult.OutcomeUncertain)
                TryQuarantineMatchingSession(preflight.Binding);
            if (concurrent is AccountDeletionResult.ConfirmedLocalCleanupIncomplete terminal)
                return CompleteConfirmedCleanup(terminal.Pending, preflight);
            if (concurrent is AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete
                uncertainTerminal)
            {
                return CompleteConfirmedCleanup(uncertainTerminal.Pending, preflight);
            }
            return concurrent;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            context.Lease.CancellationToken);
        var dispatched = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            _session.ThrowIfExpired(context.Lease);
            var operation = api.DeleteAccountAsync(request, linked.Token);
            dispatched = true;
            await operation.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!dispatched)
        {
            var retained = TryRemovePrepared(
                prepared,
                AccountDeletionTombstoneRemovalReason.CancelledBeforeDispatch);
            return new AccountDeletionResult.CancelledBeforeDispatch(preflight, retained);
        }
        catch (HttpRequestException exception) when (IsDefiniteRejection(exception.StatusCode))
        {
            var retained = TryRemovePrepared(
                prepared,
                AccountDeletionTombstoneRemovalReason.ServerRejectedPreparedRequest);
            return new AccountDeletionResult.Rejected(
                AccountDeletionRejectionReason.ServerRejected,
                preflight,
                HttpStatusCode: ToStatusCode(exception.StatusCode),
                Message: ServerMessage(exception),
                Pending: retained);
        }
        catch (OperationCanceledException exception)
        {
            TryQuarantineMatchingSession(preflight.Binding);
            var reason = RequestTimeoutClassifier.IsTimeout(exception, ct)
                ? AccountDeletionUncertainReason.RequestTimedOut
                : ct.IsCancellationRequested
                    ? AccountDeletionUncertainReason.CallerCancelledAfterDispatch
                    : AccountDeletionUncertainReason.SessionChangedAfterDispatch;
            return new AccountDeletionResult.OutcomeUncertain(
                reason,
                prepared,
                preflight,
                Message: SafeMessage(exception));
        }
        catch (HttpRequestException exception)
        {
            TryQuarantineMatchingSession(preflight.Binding);
            var reason = exception.StatusCode is HttpStatusCode.RequestTimeout
                || RequestTimeoutClassifier.IsTimeout(exception, ct)
                    ? AccountDeletionUncertainReason.RequestTimedOut
                    : exception.StatusCode is { } status && (int)status >= 500
                        ? AccountDeletionUncertainReason.ServerReturnedUncertainStatus
                        : exception.StatusCode is not null
                            ? AccountDeletionUncertainReason.ServerReturnedUncertainStatus
                            : AccountDeletionUncertainReason.TransportFailure;
            return new AccountDeletionResult.OutcomeUncertain(
                reason,
                prepared,
                preflight,
                HttpStatusCode: ToStatusCode(exception.StatusCode),
                Message: ServerMessage(exception) ?? SafeMessage(exception));
        }
        catch (Exception exception)
        {
            TryQuarantineMatchingSession(preflight.Binding);
            return new AccountDeletionResult.OutcomeUncertain(
                AccountDeletionUncertainReason.UnexpectedDispatchFailure,
                prepared,
                preflight,
                Message: SafeMessage(exception));
        }

        AccountDeletionTombstoneRecord? confirmed = null;
        var durableWitness = prepared;
        AccountDeletionCleanupFailure? transitionFailure = null;
        try
        {
            confirmed = _tombstones.TryAdvance(
                prepared,
                AccountDeletionStage.ServerConfirmed,
                _timeProvider.GetUtcNow());
            if (confirmed is null)
            {
                var read = _tombstones.Read(prepared.Binding);
                if (read.Status == AccountDeletionTombstoneReadStatus.Absent)
                {
                    var recreated = _tombstones.TryCreate(
                        prepared.Binding,
                        _timeProvider.GetUtcNow());
                    if (recreated is not null)
                    {
                        durableWitness = recreated;
                        confirmed = _tombstones.TryAdvance(
                            recreated,
                            AccountDeletionStage.ServerConfirmed,
                            _timeProvider.GetUtcNow());
                    }
                    if (confirmed is null)
                    {
                        var afterRecreate = _tombstones.Read(prepared.Binding);
                        if (afterRecreate.Status == AccountDeletionTombstoneReadStatus.Found
                            && afterRecreate.Record?.Stage
                                == AccountDeletionStage.ServerConfirmed)
                        {
                            confirmed = afterRecreate.Record;
                        }
                    }
                }
                else if (read.Status == AccountDeletionTombstoneReadStatus.Found
                    && read.Record?.Stage == AccountDeletionStage.ServerConfirmed)
                {
                    confirmed = read.Record;
                }
                else
                {
                    transitionFailure = new AccountDeletionCleanupFailure(
                        AccountDeletionCleanupTarget.Tombstone,
                        read.Status == AccountDeletionTombstoneReadStatus.Corrupt
                            ? AccountDeletionCleanupFailureKind.Corrupt
                            : read.Status == AccountDeletionTombstoneReadStatus.Unavailable
                                ? AccountDeletionCleanupFailureKind.Unavailable
                                : AccountDeletionCleanupFailureKind.Changed,
                        "The server confirmed deletion, but the durable stage could not be advanced.");
                }
            }
        }
        catch (Exception exception)
        {
            transitionFailure = new AccountDeletionCleanupFailure(
                AccountDeletionCleanupTarget.Tombstone,
                AccountDeletionCleanupFailureKind.Failed,
                SafeMessage(exception));
        }

        if (confirmed is null)
        {
            var failures = RunLocalCleanup(prepared.Binding);
            failures.Insert(
                0,
                transitionFailure ?? new AccountDeletionCleanupFailure(
                    AccountDeletionCleanupTarget.Tombstone,
                    AccountDeletionCleanupFailureKind.Changed,
                    "The server-confirmed deletion could not be durably recorded."));
            return new AccountDeletionResult.ConfirmedLocalCleanupIncomplete(
                durableWitness,
                failures,
                preflight);
        }

        return CompleteConfirmedCleanup(confirmed, preflight);
    }

    public async Task<AccountDeletionResult> ResumeConfirmedCleanupAsync(
        AccountDeletionTombstoneRecord expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        IAccountDeletionCoordinationLease coordination;
        try
        {
            coordination = await _coordinationGate.AcquireAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CreateCleanupIncomplete(
                expected,
                [new AccountDeletionCleanupFailure(
                    AccountDeletionCleanupTarget.CoordinationGate,
                    AccountDeletionCleanupFailureKind.Unavailable,
                    SafeMessage(exception))],
                preflight: null);
        }
        await using var coordinationScope = coordination;
        return await ResumeConfirmedCleanupCoreAsync(expected, ct).ConfigureAwait(false);
    }

    private Task<AccountDeletionResult> ResumeConfirmedCleanupCoreAsync(
        AccountDeletionTombstoneRecord expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ct.ThrowIfCancellationRequested();

        AccountDeletionTombstoneReadResult read;
        try
        {
            read = _tombstones.Read(expected.Binding);
        }
        catch (Exception exception)
        {
            return Task.FromResult<AccountDeletionResult>(
                CreateCleanupIncomplete(
                    expected,
                    [new AccountDeletionCleanupFailure(
                        AccountDeletionCleanupTarget.Tombstone,
                        AccountDeletionCleanupFailureKind.Unavailable,
                        SafeMessage(exception))],
                    preflight: null));
        }

        if (read.Status == AccountDeletionTombstoneReadStatus.Absent)
        {
            return Task.FromResult<AccountDeletionResult>(
                CreateCleanupIncomplete(
                    expected,
                    [new AccountDeletionCleanupFailure(
                        AccountDeletionCleanupTarget.Tombstone,
                        AccountDeletionCleanupFailureKind.Changed,
                        "The permanent account-deletion marker is absent.")],
                    preflight: null));
        }
        if (read.Status == AccountDeletionTombstoneReadStatus.Corrupt)
        {
            return Task.FromResult<AccountDeletionResult>(
                CreateCleanupIncomplete(
                    expected,
                    [new AccountDeletionCleanupFailure(
                        AccountDeletionCleanupTarget.Tombstone,
                        AccountDeletionCleanupFailureKind.Corrupt)],
                    preflight: null));
        }
        if (read.Status == AccountDeletionTombstoneReadStatus.Unavailable)
        {
            return Task.FromResult<AccountDeletionResult>(
                CreateCleanupIncomplete(
                    expected,
                    [new AccountDeletionCleanupFailure(
                        AccountDeletionCleanupTarget.Tombstone,
                        AccountDeletionCleanupFailureKind.Unavailable)],
                    preflight: null));
        }

        var current = read.Record!;
        if (current.Stage == AccountDeletionStage.Prepared)
        {
            return Task.FromResult<AccountDeletionResult>(
                new AccountDeletionResult.OutcomeUncertain(
                    AccountDeletionUncertainReason.ExistingPreparedTombstone,
                    current));
        }
        if (current != expected
            && (current.EntryId != expected.EntryId
                || current.Revision <= expected.Revision))
        {
            return Task.FromResult<AccountDeletionResult>(
                new AccountDeletionResult.Rejected(
                    AccountDeletionRejectionReason.ConcurrentStateChange,
                    Pending: current));
        }

        return Task.FromResult(CompleteConfirmedCleanup(current, preflight: null));
    }

    public async Task<AccountDeletionLocalResolutionResult> ClearLocalDataForPendingAsync(
        AccountDeletionTombstoneRecord expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        IAccountDeletionCoordinationLease coordination;
        try
        {
            coordination = await _coordinationGate.AcquireAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new AccountDeletionLocalResolutionResult(
                AccountDeletionLocalResolutionStatus.CancelledBeforeCleanup,
                expected);
        }
        catch
        {
            return new AccountDeletionLocalResolutionResult(
                AccountDeletionLocalResolutionStatus.Unavailable,
                expected);
        }
        await using var coordinationScope = coordination;
        return await ClearLocalDataForPendingCoreAsync(expected, ct).ConfigureAwait(false);
    }

    private Task<AccountDeletionLocalResolutionResult> ClearLocalDataForPendingCoreAsync(
        AccountDeletionTombstoneRecord expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(new AccountDeletionLocalResolutionResult(
                AccountDeletionLocalResolutionStatus.CancelledBeforeCleanup,
                expected));
        }
        if (expected.Stage != AccountDeletionStage.Prepared)
        {
            return Task.FromResult(new AccountDeletionLocalResolutionResult(
                AccountDeletionLocalResolutionStatus.WrongStage,
                expected));
        }

        AccountDeletionTombstoneReadResult read;
        try
        {
            read = _tombstones.Read(expected.Binding);
        }
        catch
        {
            return Task.FromResult(new AccountDeletionLocalResolutionResult(
                AccountDeletionLocalResolutionStatus.Unavailable,
                expected));
        }

        if (read.Status == AccountDeletionTombstoneReadStatus.Absent)
        {
            return Task.FromResult(new AccountDeletionLocalResolutionResult(
                AccountDeletionLocalResolutionStatus.NotFound));
        }
        if (read.Status == AccountDeletionTombstoneReadStatus.Corrupt)
        {
            return Task.FromResult(new AccountDeletionLocalResolutionResult(
                AccountDeletionLocalResolutionStatus.Corrupt,
                expected));
        }
        if (read.Status == AccountDeletionTombstoneReadStatus.Unavailable)
        {
            return Task.FromResult(new AccountDeletionLocalResolutionResult(
                AccountDeletionLocalResolutionStatus.Unavailable,
                expected));
        }
        if (read.Record != expected)
        {
            return Task.FromResult(new AccountDeletionLocalResolutionResult(
                read.Record?.Stage is AccountDeletionStage.ServerConfirmed
                    or AccountDeletionStage.LocalCleanupComplete
                    or AccountDeletionStage.UncertainLocalCleanupComplete
                    ? AccountDeletionLocalResolutionStatus.WrongStage
                    : AccountDeletionLocalResolutionStatus.Changed,
                read.Record));
        }

        var failures = RunLocalCleanup(expected.Binding);
        if (failures.Count != 0)
        {
            return Task.FromResult(new AccountDeletionLocalResolutionResult(
                AccountDeletionLocalResolutionStatus.CleanupIncomplete,
                expected,
                failures));
        }

        try
        {
            var uncertainComplete = _tombstones.TryAdvance(
                expected,
                AccountDeletionStage.UncertainLocalCleanupComplete,
                _timeProvider.GetUtcNow());
            if (uncertainComplete is not null)
            {
                return Task.FromResult(new AccountDeletionLocalResolutionResult(
                    AccountDeletionLocalResolutionStatus.Completed,
                    uncertainComplete));
            }

            var after = _tombstones.Read(expected.Binding);
            if (after.Status == AccountDeletionTombstoneReadStatus.Found
                && after.Record?.Stage == AccountDeletionStage.ServerConfirmed
                && _tombstones.TryAdvance(
                    after.Record,
                    AccountDeletionStage.LocalCleanupComplete,
                    _timeProvider.GetUtcNow()) is { } serverComplete)
            {
                return Task.FromResult(new AccountDeletionLocalResolutionResult(
                    AccountDeletionLocalResolutionStatus.Completed,
                    serverComplete));
            }
            if (after.Status == AccountDeletionTombstoneReadStatus.Found
                && after.Record?.Stage is AccountDeletionStage.LocalCleanupComplete
                    or AccountDeletionStage.UncertainLocalCleanupComplete)
            {
                return Task.FromResult(new AccountDeletionLocalResolutionResult(
                    AccountDeletionLocalResolutionStatus.Completed,
                    after.Record));
            }

            return Task.FromResult(new AccountDeletionLocalResolutionResult(
                AccountDeletionLocalResolutionStatus.Changed,
                after.Record));
        }
        catch (Exception exception)
        {
            return Task.FromResult(new AccountDeletionLocalResolutionResult(
                AccountDeletionLocalResolutionStatus.CleanupIncomplete,
                expected,
                [new AccountDeletionCleanupFailure(
                    AccountDeletionCleanupTarget.Tombstone,
                    AccountDeletionCleanupFailureKind.Failed,
                    SafeMessage(exception))]));
        }
    }

    private AccountDeletionResult CompleteConfirmedCleanup(
        AccountDeletionTombstoneRecord confirmed,
        AccountDeletionPreflight? preflight)
    {
        if (confirmed.Stage is not (
            AccountDeletionStage.ServerConfirmed
            or AccountDeletionStage.LocalCleanupComplete
            or AccountDeletionStage.UncertainLocalCleanupComplete))
        {
            return new AccountDeletionResult.OutcomeUncertain(
                AccountDeletionUncertainReason.ExistingPreparedTombstone,
                confirmed,
                preflight);
        }

        var failures = RunLocalCleanup(confirmed.Binding);
        if (failures.Count != 0)
        {
            return CreateCleanupIncomplete(
                confirmed,
                failures,
                preflight);
        }

        if (confirmed.Stage == AccountDeletionStage.UncertainLocalCleanupComplete)
        {
            return new AccountDeletionResult.OutcomeUncertainLocalCleanupComplete(
                confirmed,
                preflight);
        }
        if (confirmed.Stage == AccountDeletionStage.LocalCleanupComplete)
            return new AccountDeletionResult.Confirmed(preflight);

        try
        {
            var completed = _tombstones.TryAdvance(
                confirmed,
                AccountDeletionStage.LocalCleanupComplete,
                _timeProvider.GetUtcNow());
            if (completed is not null)
                return new AccountDeletionResult.Confirmed(preflight);

            var after = _tombstones.Read(confirmed.Binding);
            if (after.Status == AccountDeletionTombstoneReadStatus.Found
                && after.Record?.Stage == AccountDeletionStage.LocalCleanupComplete)
            {
                return new AccountDeletionResult.Confirmed(preflight);
            }

            failures.Add(new AccountDeletionCleanupFailure(
                AccountDeletionCleanupTarget.Tombstone,
                after.Status == AccountDeletionTombstoneReadStatus.Corrupt
                    ? AccountDeletionCleanupFailureKind.Corrupt
                    : after.Status == AccountDeletionTombstoneReadStatus.Unavailable
                        ? AccountDeletionCleanupFailureKind.Unavailable
                        : AccountDeletionCleanupFailureKind.Changed));
        }
        catch (Exception exception)
        {
            failures.Add(new AccountDeletionCleanupFailure(
                AccountDeletionCleanupTarget.Tombstone,
                AccountDeletionCleanupFailureKind.Failed,
                SafeMessage(exception)));
        }

        return new AccountDeletionResult.ConfirmedLocalCleanupIncomplete(
            confirmed,
            failures,
            preflight);
    }

    private List<AccountDeletionCleanupFailure> RunLocalCleanup(
        AccountDeletionBinding binding)
    {
        var failures = new List<AccountDeletionCleanupFailure>();
        if (!TryLogoutMatchingSession(binding, out var rotationFailure))
            failures.Add(rotationFailure!);

        TryCleanup(
            AccountDeletionCleanupTarget.Token,
            () => _tokenStore.Remove(binding.AccountId, binding.Endpoints),
            failures);
        TryCleanup(
            AccountDeletionCleanupTarget.VaultCache,
            () => _cacheStore.RemoveVaultCacheExact(
                binding.AccountId,
                binding.Endpoints) switch
            {
                VaultCacheRemovalStatus.Removed or VaultCacheRemovalStatus.Absent => true,
                VaultCacheRemovalStatus.Corrupt => AddCleanupFailure(
                    failures,
                    AccountDeletionCleanupTarget.VaultCache,
                    AccountDeletionCleanupFailureKind.Corrupt),
                VaultCacheRemovalStatus.Unavailable => AddCleanupFailure(
                    failures,
                    AccountDeletionCleanupTarget.VaultCache,
                    AccountDeletionCleanupFailureKind.Unavailable),
                _ => throw new InvalidDataException("Unknown vault-cache removal status."),
            },
            failures,
            addGenericFailure: false);
        TryCleanup(
            AccountDeletionCleanupTarget.DeviceKey,
            () => RemoveAccountScope(
                _deviceKeyStore.RemoveAccountScope(
                    binding.AccountId,
                    binding.Endpoints),
                AccountDeletionCleanupTarget.DeviceKey,
                failures),
            failures,
            addGenericFailure: false);
        TryCleanup(
            AccountDeletionCleanupTarget.CipherCreatePending,
            () => RemoveCipherCreatePending(binding, failures),
            failures,
            addGenericFailure: false);
        TryCleanup(
            AccountDeletionCleanupTarget.TrustedDeviceApproval,
            () => RemoveAccountScope(
                _trustedDeviceApprovalStore.RemoveAccountScope(
                    binding.AccountId,
                    binding.Endpoints),
                AccountDeletionCleanupTarget.TrustedDeviceApproval,
                failures),
            failures,
            addGenericFailure: false);
        TryCleanup(
            AccountDeletionCleanupTarget.TdeV2JitPending,
            () => RemoveAccountScope(
                _tdeV2JitPendingStore.RemoveAccountScope(
                    binding.AccountId,
                    binding.Endpoints),
                AccountDeletionCleanupTarget.TdeV2JitPending,
                failures),
            failures,
            addGenericFailure: false);
        TryCleanup(
            AccountDeletionCleanupTarget.FaviconCache,
            () => _faviconCache.ClearForAccountDeletion() switch
            {
                FaviconCacheClearStatus.Cleared or FaviconCacheClearStatus.Absent => true,
                FaviconCacheClearStatus.Unavailable => AddCleanupFailure(
                    failures,
                    AccountDeletionCleanupTarget.FaviconCache,
                    AccountDeletionCleanupFailureKind.Unavailable),
                _ => throw new InvalidDataException("Unknown favicon-cache clear status."),
            },
            failures,
            addGenericFailure: false);
        return failures;
    }

    private bool TryLogoutMatchingSession(
        AccountDeletionBinding binding,
        out AccountDeletionCleanupFailure? failure)
    {
        failure = null;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                VaultSessionLease current;
                try
                {
                    current = _session.CaptureLease();
                }
                catch (InvalidOperationException)
                {
                    return true;
                }

                if (current.AccountId != binding.AccountId
                    || current.Endpoints != binding.Endpoints)
                {
                    return true;
                }

                if (_session.TryCommit(current, _session.Logout))
                    return true;
            }

            failure = new AccountDeletionCleanupFailure(
                AccountDeletionCleanupTarget.Session,
                AccountDeletionCleanupFailureKind.Changed,
                "The matching live session changed repeatedly during logout rotation.");
            return false;
        }
        catch (Exception exception)
        {
            failure = new AccountDeletionCleanupFailure(
                AccountDeletionCleanupTarget.Session,
                AccountDeletionCleanupFailureKind.Failed,
                SafeMessage(exception));
            return false;
        }
    }

    private bool RemoveCipherCreatePending(
        AccountDeletionBinding binding,
        List<AccountDeletionCleanupFailure> failures)
    {
        var read = _cipherCreateStore.Read(binding.AccountId, binding.Endpoints);
        switch (read.Status)
        {
            case CipherCreatePendingReadStatus.Absent:
                return true;
            case CipherCreatePendingReadStatus.Corrupt:
                failures.Add(StoreFailure(
                    AccountDeletionCleanupTarget.CipherCreatePending,
                    AccountDeletionCleanupFailureKind.Corrupt));
                return false;
            case CipherCreatePendingReadStatus.Unavailable:
                failures.Add(StoreFailure(
                    AccountDeletionCleanupTarget.CipherCreatePending,
                    AccountDeletionCleanupFailureKind.Unavailable));
                return false;
            case CipherCreatePendingReadStatus.Found:
                if (_cipherCreateStore.Abandon(read.Record!))
                    return true;
                var after = _cipherCreateStore.Read(binding.AccountId, binding.Endpoints);
                if (after.Status == CipherCreatePendingReadStatus.Absent)
                    return true;
                failures.Add(StoreFailure(
                    AccountDeletionCleanupTarget.CipherCreatePending,
                    ToFailureKind(after.Status)));
                return false;
            default:
                throw new InvalidDataException("Unknown cipher-create pending read status.");
        }
    }

    private static void TryCleanup(
        AccountDeletionCleanupTarget target,
        Func<bool> action,
        List<AccountDeletionCleanupFailure> failures,
        bool addGenericFailure = true)
    {
        try
        {
            if (!action() && addGenericFailure)
            {
                failures.Add(StoreFailure(
                    target,
                    AccountDeletionCleanupFailureKind.Failed));
            }
        }
        catch (Exception exception)
        {
            failures.Add(StoreFailure(
                target,
                AccountDeletionCleanupFailureKind.Failed,
                SafeMessage(exception)));
        }
    }

    private static AccountDeletionCleanupFailure StoreFailure(
        AccountDeletionCleanupTarget target,
        AccountDeletionCleanupFailureKind kind,
        string? detail = null) => new(target, kind, detail);

    private static bool AddCleanupFailure(
        List<AccountDeletionCleanupFailure> failures,
        AccountDeletionCleanupTarget target,
        AccountDeletionCleanupFailureKind kind)
    {
        failures.Add(StoreFailure(target, kind));
        return false;
    }

    private static bool RemoveAccountScope(
        AccountScopedRemovalStatus status,
        AccountDeletionCleanupTarget target,
        List<AccountDeletionCleanupFailure> failures)
    {
        switch (status)
        {
            case AccountScopedRemovalStatus.Removed:
            case AccountScopedRemovalStatus.Absent:
                return true;
            case AccountScopedRemovalStatus.Corrupt:
                failures.Add(StoreFailure(
                    target,
                    AccountDeletionCleanupFailureKind.Corrupt));
                return false;
            case AccountScopedRemovalStatus.Unavailable:
                failures.Add(StoreFailure(
                    target,
                    AccountDeletionCleanupFailureKind.Unavailable));
                return false;
            default:
                throw new InvalidDataException("Unknown account-scope removal status.");
        }
    }

    private static AccountDeletionCleanupFailureKind ToFailureKind(
        CipherCreatePendingReadStatus status) => status switch
        {
            CipherCreatePendingReadStatus.Corrupt => AccountDeletionCleanupFailureKind.Corrupt,
            CipherCreatePendingReadStatus.Unavailable => AccountDeletionCleanupFailureKind.Unavailable,
            _ => AccountDeletionCleanupFailureKind.Changed,
        };

    private bool TryCaptureContext(
        out DeletionContext context,
        out AccountDeletionResult.Rejected rejected)
    {
        context = null!;
        VaultSessionLease lease;
        try
        {
            lease = _session.CaptureLease();
        }
        catch (InvalidOperationException)
        {
            rejected = new AccountDeletionResult.Rejected(
                AccountDeletionRejectionReason.RequiresUnlockedSession,
                Message: "Account deletion requires an active unlocked session.");
            return false;
        }

        if (!_session.IsUnlocked(lease))
        {
            rejected = new AccountDeletionResult.Rejected(
                AccountDeletionRejectionReason.RequiresUnlockedSession,
                Message: "Account deletion requires an active unlocked session.");
            return false;
        }

        PersistedSession persisted;
        try
        {
            if (!_tokenStore.TryLoad(out persisted)
                || !_session.IsCurrent(lease, persisted))
            {
                rejected = new AccountDeletionResult.Rejected(
                    AccountDeletionRejectionReason.SessionBindingMismatch,
                    Message: "The persisted session does not match the unlocked account.");
                return false;
            }
        }
        catch (Exception exception)
        {
            rejected = new AccountDeletionResult.Rejected(
                AccountDeletionRejectionReason.LocalPersistenceUnavailable,
                Message: SafeMessage(exception));
            return false;
        }

        var account = _session.Account;
        var organizations = _session.Organizations;
        if (account.Id != lease.AccountId
            || account.Endpoints != lease.Endpoints
            || !_session.IsUnlocked(lease))
        {
            rejected = new AccountDeletionResult.Rejected(
                AccountDeletionRejectionReason.SessionBindingMismatch,
                Message: "The in-memory account does not match the unlocked session.");
            return false;
        }

        AccountDeletionBinding binding;
        try
        {
            binding = new AccountDeletionBinding(
                persisted.AccountId,
                persisted.Email,
                persisted.Endpoints!,
                persisted.DeviceIdentifier);
        }
        catch (ArgumentException exception)
        {
            rejected = new AccountDeletionResult.Rejected(
                AccountDeletionRejectionReason.SessionBindingMismatch,
                Message: SafeMessage(exception));
            return false;
        }

        var claimed = account.UserIsClaimedByOrganization == true;
        var requirement = persisted.HasMasterPassword switch
        {
            true => AccountDeletionCredentialRequirement.MasterPassword,
            false => AccountDeletionCredentialRequirement.OneTimePassword,
            null => AccountDeletionCredentialRequirement.FreshOnlineAuthentication,
        };
        var blockReason = claimed
            ? AccountDeletionPreflightBlockReason.ClaimedByOrganization
            : requirement == AccountDeletionCredentialRequirement.FreshOnlineAuthentication
                ? AccountDeletionPreflightBlockReason.FreshOnlineAuthenticationRequired
                : AccountDeletionPreflightBlockReason.None;
        var ownerWarnings = organizations
            .Where(organization =>
                organization.IsMember
                && organization.Status == 2
                && organization.Type == 0)
            .GroupBy(organization => organization.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(organization => new AccountDeletionOwnerWarning(
                organization.Id,
                organization.Name,
                organization.ProductTierType))
            .ToArray();
        var preflight = new AccountDeletionPreflight(
            binding,
            requirement,
            claimed,
            ownerWarnings,
            blockReason);

        if (!_session.IsUnlocked(lease))
        {
            rejected = new AccountDeletionResult.Rejected(
                AccountDeletionRejectionReason.ConcurrentStateChange,
                preflight,
                Message: "The unlocked session changed during account deletion preflight.");
            return false;
        }

        context = new DeletionContext(lease, persisted, preflight);
        rejected = null!;
        return true;
    }

    private AccountDeletionResult? ReadPendingForDispatch(
        AccountDeletionBinding binding,
        AccountDeletionPreflight preflight)
    {
        AccountDeletionTombstoneReadAllResult read;
        try
        {
            read = _tombstones.ReadAll();
        }
        catch (Exception exception)
        {
            return new AccountDeletionResult.Rejected(
                AccountDeletionRejectionReason.LocalPersistenceUnavailable,
                preflight,
                Message: SafeMessage(exception));
        }

        if (read.Status == AccountDeletionTombstoneReadStatus.Absent)
            return null;
        if (read.Status == AccountDeletionTombstoneReadStatus.Corrupt)
        {
            return new AccountDeletionResult.Rejected(
                AccountDeletionRejectionReason.LocalPersistenceCorrupt,
                preflight);
        }
        if (read.Status == AccountDeletionTombstoneReadStatus.Unavailable)
        {
            return new AccountDeletionResult.Rejected(
                AccountDeletionRejectionReason.LocalPersistenceUnavailable,
                preflight);
        }

        var accountRecords = read.Records
            .Where(record =>
                record.Binding.AccountId == binding.AccountId
                && record.Binding.Endpoints == binding.Endpoints)
            .OrderBy(record => record.CreatedAt)
            .ToArray();
        if (accountRecords.Length == 0)
            return null;

        var terminal = accountRecords.FirstOrDefault(record =>
            record.Stage is AccountDeletionStage.ServerConfirmed
                or AccountDeletionStage.LocalCleanupComplete
                or AccountDeletionStage.UncertainLocalCleanupComplete);
        if (terminal is not null)
        {
            if (terminal.Stage == AccountDeletionStage.UncertainLocalCleanupComplete)
            {
                return new AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete(
                    terminal,
                    [new AccountDeletionCleanupFailure(
                        AccountDeletionCleanupTarget.Tombstone,
                        AccountDeletionCleanupFailureKind.Changed,
                        "Outcome-uncertain local cleanup will be checked again.")],
                    preflight);
            }
            return new AccountDeletionResult.ConfirmedLocalCleanupIncomplete(
                terminal,
                [new AccountDeletionCleanupFailure(
                    AccountDeletionCleanupTarget.Tombstone,
                    AccountDeletionCleanupFailureKind.Changed,
                    "Server-confirmed local cleanup is pending.")],
                preflight);
        }

        return new AccountDeletionResult.OutcomeUncertain(
            AccountDeletionUncertainReason.ExistingPreparedTombstone,
            accountRecords[0],
            preflight);
    }

    private static AccountDeletionResult.Rejected RejectPreflight(
        AccountDeletionPreflight preflight) => new(
        preflight.BlockReason == AccountDeletionPreflightBlockReason.ClaimedByOrganization
            ? AccountDeletionRejectionReason.ClaimedByOrganization
            : AccountDeletionRejectionReason.FreshOnlineAuthenticationRequired,
        preflight);

    private static AccountDeletionOtpRequestResult.Rejected ToOtpRejected(
        AccountDeletionResult.Rejected rejected) => new(
        rejected.Reason,
        rejected.Preflight,
        rejected.HttpStatusCode,
        rejected.Message);

    private static AccountDeletionOtpRequestResult.Rejected ToOtpRejected(
        AccountDeletionResult result) => result switch
        {
            AccountDeletionResult.Rejected rejected => ToOtpRejected(rejected),
            AccountDeletionResult.OutcomeUncertain uncertain => new(
                AccountDeletionRejectionReason.PendingDeletionExists,
                uncertain.Preflight,
                uncertain.HttpStatusCode,
                uncertain.Message),
            AccountDeletionResult.ConfirmedLocalCleanupIncomplete incomplete => new(
                AccountDeletionRejectionReason.PendingDeletionExists,
                incomplete.Preflight),
            AccountDeletionResult.OutcomeUncertainLocalCleanupComplete complete => new(
                AccountDeletionRejectionReason.PendingDeletionExists,
                complete.Preflight),
            AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete incomplete => new(
                AccountDeletionRejectionReason.PendingDeletionExists,
                incomplete.Preflight),
            _ => new AccountDeletionOtpRequestResult.Rejected(
                AccountDeletionRejectionReason.PendingDeletionExists),
        };

    private static AccountDeletionResult CreateCleanupIncomplete(
        AccountDeletionTombstoneRecord pending,
        IReadOnlyList<AccountDeletionCleanupFailure> failures,
        AccountDeletionPreflight? preflight) =>
        pending.Stage is AccountDeletionStage.Prepared
            or AccountDeletionStage.UncertainLocalCleanupComplete
            ? new AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete(
                pending,
                failures,
                preflight)
            : new AccountDeletionResult.ConfirmedLocalCleanupIncomplete(
                pending,
                failures,
                preflight);

    private DeleteAccountRequest CreateRequest(
        AccountDeletionCredential credential,
        PersistedSession persisted)
    {
        var secret = credential.Consume();
        byte[]? masterKey = null;
        try
        {
            if (credential.Kind == AccountDeletionCredentialKind.OneTimePassword)
                return DeleteAccountRequest.FromOtp(new string(secret));

            masterKey = _crypto.DeriveMasterKey(
                secret.AsSpan(),
                persisted.EffectiveMasterPasswordSalt,
                persisted.KdfType,
                persisted.KdfIterations,
                persisted.KdfMemory,
                persisted.KdfParallelism);
            var authenticationHash = _crypto.ComputeMasterPasswordHash(
                masterKey,
                secret.AsSpan());
            return DeleteAccountRequest.FromMasterPasswordHash(authenticationHash);
        }
        finally
        {
            if (masterKey is not null)
                CryptographicOperations.ZeroMemory(masterKey);
            CryptographicOperations.ZeroMemory(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(secret.AsSpan()));
        }
    }

    private AccountDeletionTombstoneRecord? TryRemovePrepared(
        AccountDeletionTombstoneRecord prepared,
        AccountDeletionTombstoneRemovalReason reason)
    {
        try
        {
            if (_tombstones.Remove(prepared, reason))
                return null;
            var read = _tombstones.Read(prepared.Binding);
            return read.Status == AccountDeletionTombstoneReadStatus.Absent
                ? null
                : read.Record ?? prepared;
        }
        catch
        {
            return prepared;
        }
    }

    private void TryQuarantineMatchingSession(AccountDeletionBinding binding)
    {
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                VaultSessionLease current;
                try
                {
                    current = _session.CaptureLease();
                }
                catch (InvalidOperationException)
                {
                    return;
                }
                if (current.AccountId != binding.AccountId
                    || current.Endpoints != binding.Endpoints)
                {
                    return;
                }
                if (_session.TryLockIfCurrent(current))
                    return;
            }
        }
        catch
        {
            // The durable Prepared tombstone remains the fail-closed recovery authority.
        }
    }

    private static bool IsDefiniteRejection(HttpStatusCode? statusCode) =>
        statusCode is HttpStatusCode.BadRequest
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound
            or HttpStatusCode.UnprocessableEntity;

    private static int? ToStatusCode(HttpStatusCode? statusCode) =>
        statusCode is null ? null : (int)statusCode.Value;

    private static string? ServerMessage(Exception exception) =>
        exception is VaultApiException api ? api.ServerMessage : null;

    private static string SafeMessage(Exception exception) => exception switch
    {
        OperationCanceledException => "The operation was cancelled.",
        HttpRequestException => "The network request did not complete.",
        IOException => "Local persistence is unavailable.",
        UnauthorizedAccessException => "Local persistence access was denied.",
        CryptographicException => "Protected local state could not be processed.",
        InvalidDataException => "Protected local state is invalid.",
        _ => "The operation could not be completed.",
    };

    private sealed record DeletionContext(
        VaultSessionLease Lease,
        PersistedSession Persisted,
        AccountDeletionPreflight Preflight);
}
