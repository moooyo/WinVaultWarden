using System.Security.Cryptography;
using System.Net;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Models;
using Core.Enums;
using Core.Services;
using Core.Session;
using Crypto;
using Crypto.V2;

namespace Vault;

public sealed class TrustedDeviceApprovalService : ITrustedDeviceApprovalService
{
    private const int AccessCodeLength = 25;
    private const int UserKeyLength = 64;
    private static readonly TimeSpan DeviceRequestLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AdministratorRequestLifetime = TimeSpan.FromDays(7);
    private const string AccessCodeAlphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private readonly object _gate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IVaultApiClientProvider _clients;
    private readonly Func<EncString, byte[], byte[]> _decryptRsa;
    private readonly FingerprintService _fingerprint;
    private readonly VaultSession _session;
    private readonly TimeProvider _timeProvider;
    private readonly ITrustedDeviceApprovalStore? _store;
    private PendingApproval? _pending;

    public TrustedDeviceApprovalService(
        IVaultApiClientProvider clients,
        CryptoService crypto,
        FingerprintService fingerprint,
        VaultSession session,
        TimeProvider? timeProvider = null,
        ITrustedDeviceApprovalStore? store = null)
        : this(
            clients,
            crypto,
            fingerprint,
            session,
            timeProvider,
            store,
            (encrypted, privateKey) => crypto.DecryptRsa(encrypted, privateKey))
    {
    }

    internal TrustedDeviceApprovalService(
        IVaultApiClientProvider clients,
        CryptoService crypto,
        FingerprintService fingerprint,
        VaultSession session,
        TimeProvider? timeProvider,
        ITrustedDeviceApprovalStore? store,
        Func<EncString, byte[], byte[]> decryptRsa)
    {
        _clients = clients;
        ArgumentNullException.ThrowIfNull(crypto);
        _decryptRsa = decryptRsa ?? throw new ArgumentNullException(nameof(decryptRsa));
        _fingerprint = fingerprint;
        _session = session;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _store = store;
    }

    public async Task<TrustedDeviceApprovalRequest> BeginAsync(
        VaultSessionLease lease,
        string email,
        string deviceIdentifier,
        CancellationToken ct = default)
        => await BeginAsync(
            lease,
            email,
            deviceIdentifier,
            TrustedDeviceApprovalMethod.Device,
            ct).ConfigureAwait(false);

    public async Task<TrustedDeviceApprovalRequest> BeginAsync(
        VaultSessionLease lease,
        string email,
        string deviceIdentifier,
        TrustedDeviceApprovalMethod method,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (!Guid.TryParseExact(deviceIdentifier, "D", out _))
            throw new ArgumentException("The device identifier is invalid.", nameof(deviceIdentifier));
        _session.ThrowIfExpired(lease);

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        byte[]? publicKey = null;
        byte[]? privateKey = null;
        ITrustedDeviceApprovalIntentLease? beginIntent = null;
        TrustedDeviceApprovalStoreRecord? storeRecord = null;
        try
        {
            ClearPending(cancel: true);
            if (method == TrustedDeviceApprovalMethod.Administrator && _store is null)
            {
                throw new TrustedDeviceApprovalException(
                    "Secure administrator device-approval storage is unavailable.");
            }
            if (method == TrustedDeviceApprovalMethod.Administrator
                && TryRestoreAdministratorPending(
                    lease,
                    email,
                    deviceIdentifier,
                    out var restored))
            {
                return restored!;
            }
            if (method == TrustedDeviceApprovalMethod.Administrator)
            {
                var binding = new DeviceKeyBinding(
                    lease.AccountId,
                    lease.Endpoints,
                    deviceIdentifier);
                beginIntent = _store!.TryAcquireBeginIntent(binding);
                if (beginIntent is null
                    && TryRestoreAdministratorPending(
                        lease,
                        email,
                        deviceIdentifier,
                        out var racedRestore))
                {
                    return racedRestore!;
                }
                if (beginIntent is null)
                {
                    throw new TrustedDeviceApprovalException(
                        "Another administrator device-approval request is being created.");
                }
            }

            using var rsa = RSA.Create(2048);
            if (rsa.KeySize != 2048)
                throw new CryptographicException("RSA-2048 is unavailable.");
            publicKey = rsa.ExportSubjectPublicKeyInfo();
            privateKey = rsa.ExportPkcs8PrivateKey();
            var publicKeyText = Convert.ToBase64String(publicKey);
            var accessCode = CreateAccessCode();
            var fingerprint = _fingerprint.GetFingerprintPhrase(publicKey, email);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, lease.CancellationToken);
            var authRequest = new AuthRequestRequest(
                accessCode,
                deviceIdentifier,
                email,
                publicKeyText,
                Type: method == TrustedDeviceApprovalMethod.Administrator ? 2 : 0);
            var response = method switch
            {
                TrustedDeviceApprovalMethod.Device =>
                    await _clients.GetAnonymous<IPublicAuthRequestApiClient>(lease.Endpoints)
                        .CreateAsync(authRequest, linked.Token).ConfigureAwait(false),
                TrustedDeviceApprovalMethod.Administrator =>
                    await _clients.GetAuthenticated<IAuthRequestApiClient>(lease)
                        .CreateAdminAsync(authRequest, linked.Token).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(method)),
            };
            if (method == TrustedDeviceApprovalMethod.Device)
            {
                linked.Token.ThrowIfCancellationRequested();
                _session.ThrowIfExpired(lease);
            }

            if (string.IsNullOrWhiteSpace(response.Id)
                || !string.Equals(response.PublicKey, publicKeyText, StringComparison.Ordinal))
            {
                throw new TrustedDeviceApprovalException(
                    "The server returned an invalid device-approval request.");
            }

            var expiresAt = _timeProvider.GetUtcNow() +
                (method == TrustedDeviceApprovalMethod.Administrator
                    ? AdministratorRequestLifetime
                    : DeviceRequestLifetime);
            if (method == TrustedDeviceApprovalMethod.Administrator
                && _store is not null)
            {
                using var durablePending = new TrustedDeviceApprovalPending(
                    response.Id,
                    privateKey,
                    lease.AccountId,
                    lease.Endpoints,
                    deviceIdentifier,
                    _timeProvider.GetUtcNow(),
                    expiresAt,
                    TrustedDeviceApprovalRequestType.AdminApproval);
                storeRecord = _store.TryCreate(beginIntent!, durablePending)
                    ?? throw new TrustedDeviceApprovalException(
                        "Another administrator device-approval request is already pending.");
                linked.Token.ThrowIfCancellationRequested();
                _session.ThrowIfExpired(lease);
            }

            var pending = new PendingApproval(
                lease,
                response.Id,
                publicKeyText,
                privateKey,
                expiresAt,
                method,
                storeRecord);
            storeRecord = null;
            privateKey = null;
            lock (_gate)
                _pending = pending;
            return new TrustedDeviceApprovalRequest(
                response.Id,
                fingerprint,
                expiresAt);
        }
        catch (Exception ex) when (
            ex is CryptographicException
                or FormatException
                or ArgumentException
                or IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or PlatformNotSupportedException)
        {
            throw new TrustedDeviceApprovalException(
                "The device-approval request could not be created.",
                ex);
        }
        finally
        {
            if (publicKey is not null)
                CryptographicOperations.ZeroMemory(publicKey);
            if (privateKey is not null)
                CryptographicOperations.ZeroMemory(privateKey);
            storeRecord?.Dispose();
            beginIntent?.Dispose();
            _operationGate.Release();
        }
    }

    public async Task<TrustedDeviceApprovalPollResult> PollAsync(
        VaultSessionLease lease,
        string requestId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        _session.ThrowIfExpired(lease);

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pending = GetPending(lease, requestId);
            if (pending.Method == TrustedDeviceApprovalMethod.Device
                && _timeProvider.GetUtcNow() >= pending.ExpiresAt)
            {
                FinalizePending(
                    pending,
                    TrustedDeviceApprovalRemovalReason.Expired,
                    cancel: false);
                return new TrustedDeviceApprovalPollResult(
                    TrustedDeviceApprovalStatus.Expired);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                lease.CancellationToken,
                pending.Cancellation.Token);
            var api = _clients.GetAuthenticated<IAuthRequestApiClient>(lease);
            AuthRequestResponse response;
            try
            {
                response = await api.GetAsync(requestId, linked.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                FinalizePending(
                    pending,
                    TrustedDeviceApprovalRemovalReason.Expired,
                    cancel: false);
                return new TrustedDeviceApprovalPollResult(
                    TrustedDeviceApprovalStatus.Expired);
            }
            linked.Token.ThrowIfCancellationRequested();
            _session.ThrowIfExpired(lease);
            EnsureCurrent(pending);
            if (response.Id != requestId
                || !string.Equals(
                    response.PublicKey,
                    pending.PublicKey,
                    StringComparison.Ordinal))
            {
                FinalizePending(
                    pending,
                    TrustedDeviceApprovalRemovalReason.Cancelled,
                    cancel: true);
                throw new TrustedDeviceApprovalException(
                    "The server returned a mismatched device-approval response.");
            }

            if (response.RequestApproved is null)
                return new TrustedDeviceApprovalPollResult(
                    TrustedDeviceApprovalStatus.Pending);
            if (response.RequestApproved == false)
            {
                FinalizePending(
                    pending,
                    TrustedDeviceApprovalRemovalReason.Completed,
                    cancel: false);
                return new TrustedDeviceApprovalPollResult(
                    TrustedDeviceApprovalStatus.Denied);
            }
            if (string.IsNullOrWhiteSpace(response.Key))
            {
                FinalizePending(
                    pending,
                    TrustedDeviceApprovalRemovalReason.Cancelled,
                    cancel: true);
                throw new TrustedDeviceApprovalException(
                    "The approved device response did not contain a user key.");
            }

            byte[]? userKey = null;
            var transferred = false;
            try
            {
                try
                {
                    var encrypted = EncString.Parse(response.Key);
                    if (!encrypted.Type.IsRsaEnvelope())
                        throw new CryptographicException("The approved user key has an invalid type.");
                    userKey = _decryptRsa(encrypted, pending.PrivateKey);
                    if (userKey.Length != UserKeyLength)
                    {
                        using (V2UserKey.ImportTransport(userKey))
                        {
                        }
                    }
                }
                catch (Exception ex) when (
                    ex is CryptographicException or FormatException or ArgumentException)
                {
                    FinalizePending(
                        pending,
                        TrustedDeviceApprovalRemovalReason.Cancelled,
                        cancel: true);
                    throw new TrustedDeviceApprovalException(
                        "The approved user key could not be decrypted.",
                        ex);
                }

                linked.Token.ThrowIfCancellationRequested();
                EnsureCurrent(pending);
                FinalizePending(
                    pending,
                    TrustedDeviceApprovalRemovalReason.Completed,
                    cancel: false);
                transferred = true;
                return new TrustedDeviceApprovalPollResult(
                    TrustedDeviceApprovalStatus.Approved,
                    userKey);
            }
            finally
            {
                if (!transferred && userKey is not null)
                    CryptographicOperations.ZeroMemory(userKey);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Cancel(VaultSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        PendingApproval? pending;
        lock (_gate)
            pending = _pending is { } candidate && candidate.Matches(lease)
                ? candidate
                : null;
        if (pending is not null)
            RemovePending(pending, cancel: true);
    }

    public void Abandon(VaultSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        PendingApproval? pending;
        lock (_gate)
            pending = _pending is { } candidate && candidate.Matches(lease)
                ? candidate
                : null;
        if (pending is not null)
        {
            FinalizePending(
                pending,
                TrustedDeviceApprovalRemovalReason.Cancelled,
                cancel: true);
        }
    }

    private bool TryRestoreAdministratorPending(
        VaultSessionLease lease,
        string email,
        string deviceIdentifier,
        out TrustedDeviceApprovalRequest? request)
    {
        request = null;
        if (_store is null)
            return false;

        var binding = new DeviceKeyBinding(
            lease.AccountId,
            lease.Endpoints,
            deviceIdentifier);
        var read = _store.Read(binding);
        if (read.Status == TrustedDeviceApprovalStoreReadStatus.Absent)
            return false;
        if (read.Status is TrustedDeviceApprovalStoreReadStatus.Corrupt
            or TrustedDeviceApprovalStoreReadStatus.Unavailable
            || read.Record is null)
        {
            throw new TrustedDeviceApprovalException(
                "The saved administrator device-approval request is unavailable.");
        }

        var record = read.Record;
        byte[]? privateKey = null;
        byte[]? publicKey = null;
        try
        {
            if (record.Pending.RequestType != TrustedDeviceApprovalRequestType.AdminApproval)
                throw new TrustedDeviceApprovalException(
                    "The saved device-approval request has an invalid type.");
            privateKey = record.Pending.CopyPrivateKeyPkcs8();
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(privateKey, out var bytesRead);
            if (bytesRead != privateKey.Length || rsa.KeySize != 2048)
                throw new CryptographicException("The saved approval private key is invalid.");
            publicKey = rsa.ExportSubjectPublicKeyInfo();
            var publicKeyText = Convert.ToBase64String(publicKey);
            var fingerprint = _fingerprint.GetFingerprintPhrase(publicKey, email);
            var pending = new PendingApproval(
                lease,
                record.Pending.RequestId,
                publicKeyText,
                privateKey,
                record.Pending.ExpiresAt,
                TrustedDeviceApprovalMethod.Administrator,
                record);
            privateKey = null;
            record = null!;
            lock (_gate)
                _pending = pending;
            request = new TrustedDeviceApprovalRequest(
                pending.Id,
                fingerprint,
                pending.ExpiresAt);
            return true;
        }
        catch (Exception ex) when (ex is
            CryptographicException
                or FormatException
                or ArgumentException
                or InvalidDataException)
        {
            throw new TrustedDeviceApprovalException(
                "The saved administrator device-approval request is invalid.",
                ex);
        }
        finally
        {
            record?.Dispose();
            if (privateKey is not null)
                CryptographicOperations.ZeroMemory(privateKey);
            if (publicKey is not null)
                CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private PendingApproval GetPending(VaultSessionLease lease, string requestId)
    {
        lock (_gate)
        {
            if (_pending is not { } pending
                || !pending.Matches(lease)
                || pending.Id != requestId)
            {
                throw new TrustedDeviceApprovalException(
                    "No matching device-approval request is pending.");
            }
            return pending;
        }
    }

    private void EnsureCurrent(PendingApproval pending)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, pending))
                throw new OperationCanceledException(pending.Cancellation.Token);
        }
    }

    private void ClearPending(bool cancel)
    {
        PendingApproval? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }
        pending?.Dispose(cancel);
    }

    private void RemovePending(PendingApproval pending, bool cancel)
    {
        if (TryDetachPending(pending))
            pending.Dispose(cancel);
    }

    private void FinalizePending(
        PendingApproval pending,
        TrustedDeviceApprovalRemovalReason reason,
        bool cancel)
    {
        if (!TryDetachPending(pending))
            throw new OperationCanceledException(pending.Cancellation.Token);

        if (pending.StoreRecord is { } record && _store is not null)
        {
            try
            {
                if (!_store.Remove(record, reason))
                    throw new TrustedDeviceApprovalException(
                        "The saved administrator device-approval request changed unexpectedly.");
                pending.MarkStoreRecordRemoved();
            }
            catch (Exception ex) when (ex is
                IOException
                    or UnauthorizedAccessException
                    or CryptographicException
                    or InvalidDataException
                    or PlatformNotSupportedException
                    or TrustedDeviceApprovalException)
            {
                pending.Dispose(cancel: true);
                throw new TrustedDeviceApprovalException(
                    "The saved administrator device-approval request could not be removed securely.",
                    ex);
            }
        }
        pending.Dispose(cancel);
    }

    private bool TryDetachPending(PendingApproval pending)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, pending))
                return false;
            _pending = null;
            return true;
        }
    }

    private static string CreateAccessCode()
    {
        return string.Create(AccessCodeLength, state: 0, static (span, _) =>
        {
            for (var index = 0; index < span.Length; index++)
            {
                span[index] = AccessCodeAlphabet[
                    RandomNumberGenerator.GetInt32(AccessCodeAlphabet.Length)];
            }
        });
    }

    private sealed class PendingApproval(
        VaultSessionLease lease,
        string id,
        string publicKey,
        byte[] privateKey,
        DateTimeOffset expiresAt,
        TrustedDeviceApprovalMethod method,
        TrustedDeviceApprovalStoreRecord? storeRecord = null)
    {
        public VaultSessionLease Lease { get; } = lease;
        public string Id { get; } = id;
        public string PublicKey { get; } = publicKey;
        public byte[] PrivateKey { get; } = privateKey;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public TrustedDeviceApprovalMethod Method { get; } = method;
        public TrustedDeviceApprovalStoreRecord? StoreRecord { get; private set; } = storeRecord;
        public CancellationTokenSource Cancellation { get; } = new();

        public bool Matches(VaultSessionLease other) =>
            Lease.SameSessionAs(other);

        public void Dispose(bool cancel)
        {
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
            Cancellation.Dispose();
            CryptographicOperations.ZeroMemory(PrivateKey);
            StoreRecord?.Dispose();
            StoreRecord = null;
        }

        public void MarkStoreRecordRemoved() => StoreRecord = null;
    }
}
