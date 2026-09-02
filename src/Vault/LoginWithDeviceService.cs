using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Crypto.V2;

namespace Vault;

/// <summary>
/// Owns the process-local secrets for the standard authenticate-and-unlock
/// auth-request flow. Nothing in this lifecycle is persisted.
/// </summary>
public sealed class LoginWithDeviceService : ILoginWithDeviceService, IDisposable
{
    private const int AccessCodeLength = 25;
    private const int RsaKeySizeBits = 2048;
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(5);
    private const string UppercaseAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string LowercaseAlphabet = "abcdefghijklmnopqrstuvwxyz";
    private const string NumberAlphabet = "0123456789";
    private const string AccessCodeAlphabet =
        UppercaseAlphabet + LowercaseAlphabet + NumberAlphabet;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly IVaultApiClientProvider _clients;
    private readonly Func<EncString, byte[], byte[]> _decryptRsa;
    private readonly FingerprintService _fingerprint;
    private readonly VaultSession _session;
    private readonly TimeProvider _timeProvider;
    private PendingRequest? _creating;
    private PendingRequest? _pending;
    private AuthenticationAttemptLease? _activeAttempt;
    private LoginWithDevicePollStatus? _terminalStatus;
    private bool _approvedConsumed;
    private long _operationGeneration;
    private bool _disposed;

    public LoginWithDeviceService(
        IVaultApiClientProvider clients,
        CryptoService crypto,
        FingerprintService fingerprint,
        VaultSession session,
        TimeProvider? timeProvider = null)
        : this(
            clients,
            crypto,
            fingerprint,
            session,
            timeProvider,
            (encrypted, privateKey) => crypto.DecryptRsa(encrypted, privateKey))
    {
    }

    internal LoginWithDeviceService(
        IVaultApiClientProvider clients,
        CryptoService crypto,
        FingerprintService fingerprint,
        VaultSession session,
        TimeProvider? timeProvider,
        Func<EncString, byte[], byte[]> decryptRsa)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        ArgumentNullException.ThrowIfNull(crypto);
        _decryptRsa = decryptRsa ?? throw new ArgumentNullException(nameof(decryptRsa));
        _fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LoginWithDeviceBeginResult> BeginAsync(
        AuthenticationAttemptLease attempt,
        ServerEndpoints endpoints,
        string email,
        string deviceIdentifier,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(endpoints);
        var normalizedEmail = NormalizeEmail(email);
        var canonicalDeviceIdentifier = NormalizeGuid(deviceIdentifier, nameof(deviceIdentifier));
        EnsureAttemptCurrent(attempt);

        var generation = StartOperation(attempt);
        using var knownDeviceCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            attempt.CancellationToken);
        var api = _clients.GetAnonymous<IPublicAuthRequestApiClient>(endpoints);
        bool isKnownDevice;
        try
        {
            isKnownDevice = await api.GetKnownDeviceAsync(
                normalizedEmail,
                canonicalDeviceIdentifier,
                knownDeviceCancellation.Token).ConfigureAwait(false);
            knownDeviceCancellation.Token.ThrowIfCancellationRequested();
            EnsureOperationCurrent(generation, attempt);
        }
        catch (OperationCanceledException ex) when (
            attempt.CancellationToken.IsCancellationRequested)
        {
            ClearEmptyOperation(generation, attempt);
            throw AttemptExpired(ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            ClearEmptyOperation(generation, attempt);
            throw AttemptExpired(ex);
        }
        catch
        {
            ClearEmptyOperation(generation, attempt);
            throw;
        }
        if (!isKnownDevice)
        {
            ClearEmptyOperation(generation, attempt);
            throw new LoginWithDeviceException(
                LoginWithDeviceFailureReason.UnknownDevice,
                "This device is not known to the account.");
        }

        byte[]? publicKey = null;
        byte[]? privateKey = null;
        char[]? accessCode = null;
        PendingRequest? creating = null;
        var published = false;
        try
        {
            using var rsa = RSA.Create(RsaKeySizeBits);
            if (rsa.KeySize != RsaKeySizeBits)
                throw new CryptographicException("RSA-2048 is unavailable.");
            publicKey = rsa.ExportSubjectPublicKeyInfo();
            privateKey = rsa.ExportPkcs8PrivateKey();
            accessCode = CreateAccessCode();

            var publicKeyText = Convert.ToBase64String(publicKey);
            var fingerprintPhrase = _fingerprint.GetFingerprintPhrase(publicKey, normalizedEmail);
            creating = new PendingRequest(
                attempt,
                endpoints,
                normalizedEmail,
                canonicalDeviceIdentifier,
                publicKeyText,
                privateKey,
                accessCode);
            privateKey = null;
            accessCode = null;
            SetCreating(generation, attempt, creating);
            using var callerCancellation = ct.Register(() => Cancel(attempt));

            using var createCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                attempt.CancellationToken,
                creating.Token);
            var startedAt = _timeProvider.GetUtcNow();
            var response = await api.CreateAsync(
                new AuthRequestRequest(
                    new string(creating.AccessCode.Span),
                    canonicalDeviceIdentifier,
                    normalizedEmail,
                    publicKeyText,
                    Type: 0),
                createCancellation.Token).ConfigureAwait(false);
            createCancellation.Token.ThrowIfCancellationRequested();
            EnsureOperationCurrent(generation, attempt, creating);
            ValidateCreateResponse(response, publicKeyText, canonicalDeviceIdentifier);

            var requestId = NormalizeResponseGuid(response.Id);
            var expiresAtUtc = startedAt + RequestLifetime;
            if (_timeProvider.GetUtcNow() >= expiresAtUtc)
            {
                CompleteCreatingAsExpired(generation, attempt, creating);
                throw new LoginWithDeviceException(
                    LoginWithDeviceFailureReason.NoPendingRequest,
                    "The device-login request expired before it could be published.");
            }

            creating.Publish(requestId, expiresAtUtc);
            Publish(generation, attempt, creating);
            creating.ArmExpiryTimer(
                _timeProvider,
                expiresAtUtc - _timeProvider.GetUtcNow(),
                () => ExpirePending(creating));
            createCancellation.Token.ThrowIfCancellationRequested();
            EnsurePublishedCurrent(generation, attempt, creating);
            published = true;
            return new LoginWithDeviceBeginResult(requestId, fingerprintPhrase, expiresAtUtc);
        }
        catch (OperationCanceledException ex) when (attempt.CancellationToken.IsCancellationRequested)
        {
            if (creating is not null)
                RemoveOwnedRequest(creating, cancel: true);
            throw AttemptExpired(ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            if (creating is not null)
                RemoveOwnedRequest(creating, cancel: true);
            throw AttemptExpired(ex);
        }
        catch (OperationCanceledException)
        {
            if (creating is not null)
                RemoveOwnedRequest(creating, cancel: true);
            throw;
        }
        catch (LoginWithDeviceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is
            CryptographicException
                or FormatException
                or ArgumentException
                or InvalidDataException
                or JsonException
                or ObjectDisposedException)
        {
            if (creating is not null)
                RemoveOwnedRequest(creating, cancel: true);
            throw new LoginWithDeviceException(
                LoginWithDeviceFailureReason.InvalidServerResponse,
                "The device-login request could not be created securely.",
                ex);
        }
        finally
        {
            if (publicKey is not null)
                CryptographicOperations.ZeroMemory(publicKey);
            if (privateKey is not null)
                CryptographicOperations.ZeroMemory(privateKey);
            if (accessCode is not null)
                Array.Clear(accessCode);
            if (!published && creating is not null)
                RemoveOwnedRequest(creating, cancel: true);
        }
    }

    public async Task<LoginWithDevicePollResult> PollAsync(
        AuthenticationAttemptLease attempt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        EnsureAttemptCurrent(attempt);
        using var gateCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            attempt.CancellationToken);
        try
        {
            await _pollGate.WaitAsync(gateCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (attempt.CancellationToken.IsCancellationRequested)
        {
            Cancel(attempt);
            throw AttemptExpired(ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Cancel(attempt);
            throw AttemptExpired(ex);
        }
        catch (OperationCanceledException)
        {
            Cancel(attempt);
            throw;
        }

        try
        {
            if (TryReadTerminal(attempt, out var terminal))
                return new LoginWithDevicePollResult(terminal);
            var pending = GetPending(attempt);
            if (TryExpire(pending))
                return new LoginWithDevicePollResult(LoginWithDevicePollStatus.Expired);
            using var callerCancellation = ct.Register(() => Cancel(attempt));

            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                attempt.CancellationToken,
                pending.Token);
            AuthRequestResponse response;
            try
            {
                response = await _clients
                    .GetAnonymous<IPublicAuthRequestApiClient>(pending.Endpoints)
                    .GetResponseAsync(
                        pending.RequestId,
                        new string(pending.AccessCode.Span),
                        requestCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                Complete(pending, LoginWithDevicePollStatus.Expired, cancel: false);
                return new LoginWithDevicePollResult(LoginWithDevicePollStatus.Expired);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                Complete(pending, LoginWithDevicePollStatus.Denied, cancel: false);
                return new LoginWithDevicePollResult(LoginWithDevicePollStatus.Denied);
            }
            catch (HttpRequestException ex) when (IsTerminalClientError(ex.StatusCode))
            {
                Complete(pending, null, cancel: true);
                throw new LoginWithDeviceException(
                    LoginWithDeviceFailureReason.InvalidServerResponse,
                    "The server rejected the pending device-login request.",
                    ex);
            }
            catch (Exception ex) when (ex is
                JsonException or FormatException or InvalidDataException or ArgumentException)
            {
                Complete(pending, null, cancel: true);
                throw new LoginWithDeviceException(
                    LoginWithDeviceFailureReason.InvalidServerResponse,
                    "The server returned a malformed device-login response.",
                    ex);
            }
            catch (OperationCanceledException ex) when (attempt.CancellationToken.IsCancellationRequested)
            {
                throw AttemptExpired(ex);
            }
            catch (OperationCanceledException)
            {
                if (TryReadTerminal(attempt, out terminal))
                    return new LoginWithDevicePollResult(terminal);
                if (ct.IsCancellationRequested)
                {
                    Cancel(attempt);
                    throw;
                }
                if (pending.Token.IsCancellationRequested)
                    throw AttemptExpired(new OperationCanceledException(
                        "The pending device-login request was invalidated."));
                // A transport timeout does not consume the server request.
                throw;
            }

            try
            {
                requestCancellation.Token.ThrowIfCancellationRequested();
                EnsurePendingCurrent(pending);
                if (TryExpire(pending))
                    return new LoginWithDevicePollResult(LoginWithDevicePollStatus.Expired);
                return ProcessResponse(
                    attempt,
                    pending,
                    response,
                    requestCancellation.Token,
                    ct);
            }
            catch (OperationCanceledException ex) when (
                attempt.CancellationToken.IsCancellationRequested)
            {
                throw AttemptExpired(ex);
            }
            catch (OperationCanceledException ex)
            {
                if (TryReadTerminal(attempt, out terminal))
                    return new LoginWithDevicePollResult(terminal);
                if (ct.IsCancellationRequested)
                    throw;
                if (pending.Token.IsCancellationRequested)
                    throw AttemptExpired(ex);
                // Preserve the pending request for a transport-level timeout.
                throw;
            }
        }
        finally
        {
            _pollGate.Release();
        }
    }

    public void Cancel(AuthenticationAttemptLease attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        PendingRequest? creating;
        PendingRequest? pending;
        lock (_gate)
        {
            if (!ReferenceEquals(_activeAttempt, attempt))
                return;
            _operationGeneration = NextGeneration(_operationGeneration);
            _activeAttempt = null;
            _terminalStatus = null;
            _approvedConsumed = false;
            creating = _creating;
            pending = _pending;
            _creating = null;
            _pending = null;
        }
        creating?.Dispose(cancel: true);
        if (!ReferenceEquals(creating, pending))
            pending?.Dispose(cancel: true);
    }

    public void Dispose()
    {
        PendingRequest? creating;
        PendingRequest? pending;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _operationGeneration = NextGeneration(_operationGeneration);
            _activeAttempt = null;
            _terminalStatus = null;
            _approvedConsumed = false;
            creating = _creating;
            pending = _pending;
            _creating = null;
            _pending = null;
        }
        creating?.Dispose(cancel: true);
        if (!ReferenceEquals(creating, pending))
            pending?.Dispose(cancel: true);
    }

    private LoginWithDevicePollResult ProcessResponse(
        AuthenticationAttemptLease attempt,
        PendingRequest pending,
        AuthRequestResponse response,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken)
    {
        try
        {
            ValidatePollResponse(response, pending);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            Complete(pending, null, cancel: true);
            throw new LoginWithDeviceException(
                LoginWithDeviceFailureReason.InvalidServerResponse,
                "The server returned a mismatched device-login response.",
                ex);
        }

        if (response.ResponseDate is null)
        {
            if (response.RequestApproved is null or false)
                return new LoginWithDevicePollResult(LoginWithDevicePollStatus.Pending);
            return FailInvalidApproval(
                pending,
                "An approved device-login response is missing its response date.");
        }
        if (!TryParseProtocolDate(response.ResponseDate, out _))
            return FailInvalidApproval(pending, "The device-login response date is invalid.");
        if (response.RequestApproved == false)
        {
            Complete(pending, LoginWithDevicePollStatus.Denied, cancel: false);
            return new LoginWithDevicePollResult(LoginWithDevicePollStatus.Denied);
        }
        if (response.RequestApproved is null)
        {
            return FailInvalidApproval(
                pending,
                "An answered device-login response is missing its approval decision.");
        }
        if (string.IsNullOrWhiteSpace(response.Key))
        {
            return FailInvalidApproval(
                pending,
                "The approved device-login response is missing its user key.");
        }

        byte[]? userKey = null;
        try
        {
            var encrypted = EncString.Parse(response.Key);
            if (!encrypted.Type.IsRsaEnvelope())
                throw new CryptographicException("The approved user key is not an RSA envelope.");
            userKey = _decryptRsa(encrypted, pending.PrivateKey);
            var securityVersion = ValidateUserKey(userKey);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAttemptCurrent(attempt);
            if (TryExpire(pending))
                return new LoginWithDevicePollResult(LoginWithDevicePollStatus.Expired);

            var grant = CreateGrant(pending, userKey, securityVersion);
            userKey = null;
            try
            {
                if (attempt.CancellationToken.IsCancellationRequested)
                    throw AttemptExpired(new OperationCanceledException(attempt.CancellationToken));
                callerCancellationToken.ThrowIfCancellationRequested();
                EnsureAttemptCurrent(attempt);
                return new LoginWithDevicePollResult(LoginWithDevicePollStatus.Approved, grant);
            }
            catch
            {
                grant.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException ex) when (attempt.CancellationToken.IsCancellationRequested)
        {
            throw AttemptExpired(ex);
        }
        catch (OperationCanceledException)
        {
            if (TryReadTerminal(attempt, out var terminal))
                return new LoginWithDevicePollResult(terminal);
            if (callerCancellationToken.IsCancellationRequested)
            {
                Cancel(attempt);
                throw;
            }
            if (pending.Token.IsCancellationRequested)
                throw AttemptExpired(new OperationCanceledException(
                    "The pending device-login request was invalidated."));
            throw;
        }
        catch (LoginWithDeviceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is
            CryptographicException
                or FormatException
                or ArgumentException
                or ObjectDisposedException)
        {
            if (callerCancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(callerCancellationToken);
            if (TryReadTerminal(attempt, out var terminal))
                return new LoginWithDevicePollResult(terminal);
            if (attempt.CancellationToken.IsCancellationRequested
                || pending.Token.IsCancellationRequested)
            {
                throw AttemptExpired(ex);
            }
            CompleteIfOwned(pending, null, cancel: true);
            throw new LoginWithDeviceException(
                LoginWithDeviceFailureReason.InvalidServerResponse,
                "The approved device-login user key is invalid.",
                ex);
        }
        finally
        {
            if (userKey is not null)
                CryptographicOperations.ZeroMemory(userKey);
        }
    }

    private long StartOperation(AuthenticationAttemptLease attempt)
    {
        PendingRequest? creating;
        PendingRequest? pending;
        long generation;
        lock (_gate)
        {
            ThrowIfDisposed();
            _operationGeneration = NextGeneration(_operationGeneration);
            generation = _operationGeneration;
            _activeAttempt = attempt;
            _terminalStatus = null;
            _approvedConsumed = false;
            creating = _creating;
            pending = _pending;
            _creating = null;
            _pending = null;
        }
        creating?.Dispose(cancel: true);
        if (!ReferenceEquals(creating, pending))
            pending?.Dispose(cancel: true);
        return generation;
    }

    private void SetCreating(
        long generation,
        AuthenticationAttemptLease attempt,
        PendingRequest creating)
    {
        lock (_gate)
        {
            EnsureOperationCurrentNoLock(generation, attempt);
            _creating = creating;
        }
        creating.RegisterAttemptInvalidation(
            attempt.CancellationToken,
            () => InvalidateFromAttempt(creating));
    }

    private void Publish(
        long generation,
        AuthenticationAttemptLease attempt,
        PendingRequest creating)
    {
        lock (_gate)
        {
            EnsureOperationCurrentNoLock(generation, attempt);
            if (!ReferenceEquals(_creating, creating))
                throw new OperationCanceledException(creating.Token);
            _creating = null;
            _pending = creating;
        }
    }

    private void ClearEmptyOperation(
        long generation,
        AuthenticationAttemptLease attempt)
    {
        lock (_gate)
        {
            if (_operationGeneration == generation
                && ReferenceEquals(_activeAttempt, attempt)
                && _creating is null
                && _pending is null)
            {
                _activeAttempt = null;
            }
        }
    }

    private void CompleteCreatingAsExpired(
        long generation,
        AuthenticationAttemptLease attempt,
        PendingRequest creating)
    {
        lock (_gate)
        {
            EnsureOperationCurrentNoLock(generation, attempt);
            if (!ReferenceEquals(_creating, creating))
                throw new OperationCanceledException(creating.Token);
            _creating = null;
            _terminalStatus = LoginWithDevicePollStatus.Expired;
        }
        creating.Dispose(cancel: false);
    }

    private PendingRequest GetPending(AuthenticationAttemptLease attempt)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_approvedConsumed && ReferenceEquals(_activeAttempt, attempt))
            {
                throw new LoginWithDeviceException(
                    LoginWithDeviceFailureReason.NoPendingRequest,
                    "The approved device-login grant has already been consumed.");
            }
            if (_pending is not { } pending
                || !ReferenceEquals(_activeAttempt, attempt)
                || !ReferenceEquals(pending.Attempt, attempt))
            {
                throw new LoginWithDeviceException(
                    LoginWithDeviceFailureReason.NoPendingRequest,
                    "No matching device-login request is pending.");
            }
            return pending;
        }
    }

    private void EnsurePendingCurrent(PendingRequest pending)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(_pending, pending)
                || !ReferenceEquals(_activeAttempt, pending.Attempt))
            {
                throw new OperationCanceledException(pending.Token);
            }
            if (pending.Attempt.CancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(pending.Attempt.CancellationToken);
        }
        EnsureAttemptCurrent(pending.Attempt);
    }

    private bool TryExpire(PendingRequest pending)
    {
        if (_timeProvider.GetUtcNow() < pending.ExpiresAtUtc)
            return false;
        CompleteIfOwned(pending, LoginWithDevicePollStatus.Expired, cancel: true);
        return true;
    }

    private void ExpirePending(PendingRequest pending)
    {
        if (TryDetach(pending, LoginWithDevicePollStatus.Expired, approvedConsumed: false))
            pending.Dispose(cancel: true);
    }

    private void InvalidateFromAttempt(PendingRequest request)
    {
        PendingRequest? detached = null;
        lock (_gate)
        {
            if (ReferenceEquals(_creating, request))
            {
                _creating = null;
                detached = request;
            }
            if (ReferenceEquals(_pending, request))
            {
                _pending = null;
                detached = request;
            }
            if (ReferenceEquals(_activeAttempt, request.Attempt))
            {
                _operationGeneration = NextGeneration(_operationGeneration);
                _activeAttempt = null;
                _terminalStatus = null;
                _approvedConsumed = false;
            }
        }
        detached?.Dispose(cancel: true);
    }

    private void Complete(
        PendingRequest pending,
        LoginWithDevicePollStatus? terminal,
        bool cancel)
    {
        if (!TryDetach(pending, terminal, approvedConsumed: false))
        {
            throw AttemptExpired(new OperationCanceledException(
                "The pending device-login request was invalidated.",
                pending.Token));
        }
        pending.Dispose(cancel);
    }

    private void CompleteIfOwned(
        PendingRequest pending,
        LoginWithDevicePollStatus? terminal,
        bool cancel)
    {
        if (TryDetach(pending, terminal, approvedConsumed: false))
            pending.Dispose(cancel);
    }

    private void RemoveOwnedRequest(PendingRequest request, bool cancel)
    {
        _ = TryDetach(request, null, approvedConsumed: false);
        request.Dispose(cancel);
    }

    private bool TryDetach(
        PendingRequest request,
        LoginWithDevicePollStatus? terminal,
        bool approvedConsumed)
    {
        lock (_gate)
        {
            var owned = false;
            if (ReferenceEquals(_creating, request))
            {
                _creating = null;
                owned = true;
            }
            if (ReferenceEquals(_pending, request))
            {
                _pending = null;
                owned = true;
            }
            if (!owned)
                return false;
            _terminalStatus = terminal;
            _approvedConsumed = approvedConsumed;
            return true;
        }
    }

    private LoginWithDeviceGrant CreateGrant(
        PendingRequest pending,
        byte[] userKey,
        int securityVersion)
    {
        char[]? accessCode = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(_pending, pending)
                || !ReferenceEquals(_activeAttempt, pending.Attempt)
                || pending.Attempt.CancellationToken.IsCancellationRequested
                || _timeProvider.GetUtcNow() >= pending.ExpiresAtUtc)
            {
                throw new OperationCanceledException(pending.Token);
            }
            _pending = null;
            _terminalStatus = null;
            _approvedConsumed = true;
            accessCode = pending.DetachAccessCode();
        }

        try
        {
            var grant = new LoginWithDeviceGrant(
                pending.RequestId,
                accessCode,
                pending.Email,
                pending.Endpoints,
                pending.DeviceIdentifier,
                userKey,
                securityVersion);
            accessCode = null;
            pending.Dispose(cancel: false);
            return grant;
        }
        finally
        {
            if (accessCode is not null)
                Array.Clear(accessCode);
        }
    }

    private LoginWithDevicePollResult FailInvalidApproval(
        PendingRequest pending,
        string message)
    {
        Complete(pending, null, cancel: true);
        throw new LoginWithDeviceException(
            LoginWithDeviceFailureReason.InvalidServerResponse,
            message);
    }

    private bool TryReadTerminal(
        AuthenticationAttemptLease attempt,
        out LoginWithDevicePollStatus status)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeAttempt, attempt)
                && _terminalStatus is { } terminal)
            {
                status = terminal;
                return true;
            }
        }
        status = default;
        return false;
    }

    private void EnsureOperationCurrent(
        long generation,
        AuthenticationAttemptLease attempt,
        PendingRequest? creating = null)
    {
        lock (_gate)
        {
            EnsureOperationCurrentNoLock(generation, attempt);
            if (creating is not null && !ReferenceEquals(_creating, creating))
                throw new OperationCanceledException(creating.Token);
        }
        EnsureAttemptCurrent(attempt);
    }

    private void EnsurePublishedCurrent(
        long generation,
        AuthenticationAttemptLease attempt,
        PendingRequest pending)
    {
        lock (_gate)
        {
            EnsureOperationCurrentNoLock(generation, attempt);
            if (!ReferenceEquals(_pending, pending))
                throw new OperationCanceledException(pending.Token);
        }
        EnsureAttemptCurrent(attempt);
    }

    private void EnsureOperationCurrentNoLock(
        long generation,
        AuthenticationAttemptLease attempt)
    {
        ThrowIfDisposed();
        if (_operationGeneration != generation
            || !ReferenceEquals(_activeAttempt, attempt)
            || attempt.CancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(attempt.CancellationToken);
        }
    }

    private void EnsureAttemptCurrent(AuthenticationAttemptLease attempt)
    {
        if (!_session.IsCurrent(attempt))
        {
            throw AttemptExpired(new OperationCanceledException(
                "The authentication attempt is no longer current.",
                attempt.CancellationToken));
        }
    }

    private static void ValidateCreateResponse(
        AuthRequestResponse response,
        string publicKey,
        string deviceIdentifier)
    {
        ArgumentNullException.ThrowIfNull(response);
        _ = NormalizeResponseGuid(response.Id);
        if (!string.Equals(response.PublicKey, publicKey, StringComparison.Ordinal))
            throw new FormatException("The auth-request public key does not match.");
        ValidateOptionalDeviceIdentifier(response.RequestDeviceIdentifier, deviceIdentifier);
        if (response.ResponseDate is not null
            || response.RequestApproved == true
            || response.Key is not null)
        {
            throw new FormatException("A newly created auth request is already answered.");
        }
    }

    private static void ValidatePollResponse(
        AuthRequestResponse response,
        PendingRequest pending)
    {
        ArgumentNullException.ThrowIfNull(response);
        var responseId = NormalizeResponseGuid(response.Id);
        if (!string.Equals(responseId, pending.RequestId, StringComparison.Ordinal)
            || !string.Equals(response.PublicKey, pending.PublicKey, StringComparison.Ordinal))
        {
            throw new FormatException("The auth-request binding does not match.");
        }
        ValidateOptionalDeviceIdentifier(
            response.RequestDeviceIdentifier,
            pending.DeviceIdentifier);
    }

    private static void ValidateOptionalDeviceIdentifier(
        string? responseDeviceIdentifier,
        string expectedDeviceIdentifier)
    {
        if (responseDeviceIdentifier is null)
            return;
        var canonical = NormalizeGuid(responseDeviceIdentifier, nameof(responseDeviceIdentifier));
        if (!string.Equals(canonical, expectedDeviceIdentifier, StringComparison.Ordinal))
            throw new FormatException("The auth-request device identifier does not match.");
    }

    private static int ValidateUserKey(byte[] userKey)
    {
        ArgumentNullException.ThrowIfNull(userKey);
        if (userKey.Length == 64)
            return 1;
        using (V2UserKey.ImportTransport(userKey))
        {
        }
        return 2;
    }

    private static bool TryParseProtocolDate(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);

    private static bool IsTerminalClientError(HttpStatusCode? statusCode)
    {
        var code = statusCode is null ? 0 : (int)statusCode.Value;
        return code is >= 300 and < 408 or > 408 and < 429;
    }

    private static LoginWithDeviceException AttemptExpired(Exception innerException) =>
        new(
            LoginWithDeviceFailureReason.NoPendingRequest,
            "The authentication attempt expired while waiting for device approval.",
            innerException);

    private static string NormalizeEmail(string email)
    {
        var normalized = MasterPasswordSaltPolicy.NormalizeEmail(email);
        MasterPasswordSaltPolicy.Validate(normalized);
        return normalized;
    }

    private static string NormalizeResponseGuid(string value)
    {
        var canonical = NormalizeGuid(value, nameof(value));
        if (!string.Equals(value, canonical, StringComparison.Ordinal))
            throw new FormatException("The auth-request identifier is not canonical.");
        return canonical;
    }

    private static string NormalizeGuid(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty)
            throw new FormatException("The value must be a non-empty GUID in D format.");
        return parsed.ToString("D");
    }

    private static char[] CreateAccessCode()
    {
        var result = new char[AccessCodeLength];
        result[0] = UppercaseAlphabet[RandomNumberGenerator.GetInt32(UppercaseAlphabet.Length)];
        result[1] = LowercaseAlphabet[RandomNumberGenerator.GetInt32(LowercaseAlphabet.Length)];
        result[2] = NumberAlphabet[RandomNumberGenerator.GetInt32(NumberAlphabet.Length)];
        for (var index = 3; index < result.Length; index++)
        {
            result[index] = AccessCodeAlphabet[
                RandomNumberGenerator.GetInt32(AccessCodeAlphabet.Length)];
        }
        for (var index = result.Length - 1; index > 0; index--)
        {
            var swapIndex = RandomNumberGenerator.GetInt32(index + 1);
            (result[index], result[swapIndex]) = (result[swapIndex], result[index]);
        }
        return result;
    }

    private static long NextGeneration(long current) =>
        current == long.MaxValue ? long.MaxValue : current + 1;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class PendingRequest : IDisposable
    {
        private readonly object _secretGate = new();
        private char[]? _accessCode;
        private byte[]? _privateKey;
        private CancellationTokenRegistration _attemptRegistration;
        private ITimer? _expiryTimer;
        private int _disposed;

        public PendingRequest(
            AuthenticationAttemptLease attempt,
            ServerEndpoints endpoints,
            string email,
            string deviceIdentifier,
            string publicKey,
            byte[] privateKey,
            char[] accessCode)
        {
            Attempt = attempt;
            Endpoints = endpoints;
            Email = email;
            DeviceIdentifier = deviceIdentifier;
            PublicKey = publicKey;
            _privateKey = privateKey;
            _accessCode = accessCode;
            Token = Cancellation.Token;
        }

        public AuthenticationAttemptLease Attempt { get; }
        public ServerEndpoints Endpoints { get; }
        public string Email { get; }
        public string DeviceIdentifier { get; }
        public string PublicKey { get; }
        public string RequestId { get; private set; } = string.Empty;
        public DateTimeOffset ExpiresAtUtc { get; private set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken Token { get; }

        public ReadOnlyMemory<char> AccessCode
        {
            get
            {
                lock (_secretGate)
                {
                    ThrowIfDisposed();
                    return _accessCode!;
                }
            }
        }

        public byte[] PrivateKey
        {
            get
            {
                lock (_secretGate)
                {
                    ThrowIfDisposed();
                    return _privateKey!;
                }
            }
        }

        public void Publish(string requestId, DateTimeOffset expiresAtUtc)
        {
            RequestId = requestId;
            ExpiresAtUtc = expiresAtUtc;
        }

        public void RegisterAttemptInvalidation(
            CancellationToken cancellationToken,
            Action callback)
        {
            _attemptRegistration = cancellationToken.Register(callback);
        }

        public void ArmExpiryTimer(
            TimeProvider timeProvider,
            TimeSpan dueTime,
            Action callback)
        {
            var timer = timeProvider.CreateTimer(
                static state => ((Action)state!).Invoke(),
                callback,
                dueTime > TimeSpan.Zero ? dueTime : TimeSpan.Zero,
                Timeout.InfiniteTimeSpan);
            lock (_secretGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    timer.Dispose();
                    return;
                }
                _expiryTimer = timer;
            }
        }

        public char[] DetachAccessCode()
        {
            lock (_secretGate)
            {
                ThrowIfDisposed();
                return Interlocked.Exchange(ref _accessCode, null)
                    ?? throw new ObjectDisposedException(nameof(PendingRequest));
            }
        }

        public void Dispose(bool cancel)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (cancel)
            {
                try
                {
                    Cancellation.Cancel();
                }
                catch (AggregateException)
                {
                }
            }

            _expiryTimer?.Dispose();
            _attemptRegistration.Unregister();
            lock (_secretGate)
            {
                if (_accessCode is not null)
                    Array.Clear(_accessCode);
                if (_privateKey is not null)
                    CryptographicOperations.ZeroMemory(_privateKey);
                _accessCode = null;
                _privateKey = null;
            }
            Cancellation.Dispose();
        }

        public void Dispose() => Dispose(cancel: true);

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
