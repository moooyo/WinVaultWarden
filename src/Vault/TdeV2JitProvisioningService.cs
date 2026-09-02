using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto.V2;

namespace Vault;

public sealed class TdeV2JitProvisioningService : ITdeV2JitProvisioningService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IVaultApiClientProvider _clients;
    private readonly IDeviceKeyStore _deviceKeys;
    private readonly ITokenStore _tokenStore;
    private readonly VaultSession _session;
    private readonly VaultBootstrapper _bootstrapper;
    private readonly IDeviceTrustService _deviceTrust;
    private readonly ITdeV2JitPendingStore _pendingStore;

    public TdeV2JitProvisioningService(
        IVaultApiClientProvider clients,
        IDeviceKeyStore deviceKeys,
        ITokenStore tokenStore,
        VaultSession session,
        VaultBootstrapper bootstrapper,
        IDeviceTrustService deviceTrust,
        ITdeV2JitPendingStore pendingStore)
    {
        _clients = clients;
        _deviceKeys = deviceKeys;
        _tokenStore = tokenStore;
        _session = session;
        _bootstrapper = bootstrapper;
        _deviceTrust = deviceTrust;
        _pendingStore = pendingStore;
    }

    public async Task<TdeV2JitProvisioningResult> ProvisionAsync(
        VaultSessionLease authenticatedSession,
        string organizationIdentifier,
        bool trustCurrentDevice,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authenticatedSession);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationIdentifier);
        var resume = TryGetPendingResumeContext(
            authenticatedSession,
            organizationIdentifier);
        if (resume is not null)
        {
            return await ProvisionResolvedAsync(
                authenticatedSession,
                resume.OrganizationId,
                authenticatedSession.AccountId,
                ReadOnlyMemory<byte>.Empty,
                resume.TrustCurrentDevice,
                resume.OrganizationIdentifier,
                ct).ConfigureAwait(false);
        }

        var api = _clients.GetAuthenticated<ITdeRegistrationApiClient>(authenticatedSession);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            authenticatedSession.CancellationToken);
        OrganizationAutoEnrollStatusResponse status;
        OrganizationKeysResponse keys;
        try
        {
            status = await api.GetAutoEnrollStatusAsync(
                organizationIdentifier,
                linked.Token).ConfigureAwait(false);
            if (!status.ResetPasswordEnabled || string.IsNullOrWhiteSpace(status.Id))
            {
                throw Failure(
                    TdeV2JitProvisioningStage.CapabilityCheck,
                    "The SSO organization is not eligible for automatic TDE enrollment.");
            }
            keys = await api.GetOrganizationKeysAsync(
                status.Id,
                linked.Token).ConfigureAwait(false);
        }
        catch (TdeV2JitProvisioningException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedRemoteFailure(ex) || ex is ArgumentException)
        {
            throw Failure(
                TdeV2JitProvisioningStage.CapabilityCheck,
                "The SSO organization enrollment context could not be resolved.",
                ex);
        }

        if (string.IsNullOrWhiteSpace(keys.PublicKey))
        {
            throw Failure(
                TdeV2JitProvisioningStage.CapabilityCheck,
                "The SSO organization has no public recovery key.");
        }
        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(keys.PublicKey);
            if (!string.Equals(Convert.ToBase64String(publicKey), keys.PublicKey, StringComparison.Ordinal))
                throw new FormatException("The organization public key is not canonical Base64.");
        }
        catch (FormatException ex)
        {
            throw Failure(
                TdeV2JitProvisioningStage.CapabilityCheck,
                "The SSO organization public recovery key is invalid.",
                ex);
        }

        try
        {
            return await ProvisionResolvedAsync(
                authenticatedSession,
                status.Id!,
                authenticatedSession.AccountId,
                publicKey,
                trustCurrentDevice,
                organizationIdentifier,
                linked.Token).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private PendingResumeContext? TryGetPendingResumeContext(
        VaultSessionLease lease,
        string organizationIdentifier)
    {
        if (!_tokenStore.TryLoad(out var persisted)
            || !_session.IsCurrent(lease, persisted))
        {
            return null;
        }

        var binding = new DeviceKeyBinding(
            lease.AccountId,
            lease.Endpoints,
            persisted.DeviceIdentifier);
        var record = ReadPending(binding, TdeV2JitProvisioningStage.Preparation);
        if (record is null)
            return null;
        try
        {
            if (!string.Equals(
                    record.Pending.OrganizationIdentifier,
                    organizationIdentifier,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    PendingFailureStage(record.Stage),
                    "The SSO organization does not match the saved V2 TDE JIT enrollment.");
            }
            return new PendingResumeContext(
                record.Pending.OrganizationId,
                record.Pending.OrganizationIdentifier,
                record.Pending.TrustCurrentDevice);
        }
        finally
        {
            record.Dispose();
        }
    }

    public async Task<TdeV2JitProvisioningResult> ProvisionAsync(
        VaultSessionLease authenticatedSession,
        string organizationId,
        string userId,
        ReadOnlyMemory<byte> organizationPublicKeySpki,
        bool trustCurrentDevice,
        CancellationToken ct = default)
        => await ProvisionResolvedAsync(
            authenticatedSession,
            organizationId,
            userId,
            organizationPublicKeySpki,
            trustCurrentDevice,
            organizationId,
            ct).ConfigureAwait(false);

    private async Task<TdeV2JitProvisioningResult> ProvisionResolvedAsync(
        VaultSessionLease authenticatedSession,
        string organizationId,
        string userId,
        ReadOnlyMemory<byte> organizationPublicKeySpki,
        bool trustCurrentDevice,
        string organizationIdentifier,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authenticatedSession);
        var normalizedOrganizationId = RequireGuid(organizationId, nameof(organizationId));
        var normalizedUserId = RequireGuid(userId, nameof(userId));
        if (!string.Equals(
                normalizedUserId,
                RequireGuid(authenticatedSession.AccountId, nameof(authenticatedSession)),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The provisioning user does not match the authenticated account.",
                nameof(userId));
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ProvisionCoreAsync(
                authenticatedSession,
                normalizedOrganizationId,
                normalizedUserId,
                organizationPublicKeySpki,
                trustCurrentDevice,
                organizationIdentifier,
                ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TdeV2JitProvisioningResult> ProvisionCoreAsync(
        VaultSessionLease lease,
        string organizationId,
        string userId,
        ReadOnlyMemory<byte> organizationPublicKeySpki,
        bool trustCurrentDevice,
        string organizationIdentifier,
        CancellationToken ct)
    {
        if (_session.State != VaultState.Locked
            || !_tokenStore.TryLoad(out var persisted)
            || !_session.IsCurrent(lease, persisted)
            || persisted.Endpoints is null)
        {
            throw Failure(
                TdeV2JitProvisioningStage.CapabilityCheck,
                "No matching authenticated JIT provisioning session is available.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            lease.CancellationToken);
        var binding = new DeviceKeyBinding(
            lease.AccountId,
            lease.Endpoints,
            persisted.DeviceIdentifier);
        var mustResume = PendingExists(binding);
        if (!mustResume)
        {
            ConfigResponse config;
            try
            {
                config = await _clients
                    .GetAnonymous<IServerConfigApiClient>(lease.Endpoints)
                    .GetConfigAsync(linked.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpectedRemoteFailure(ex))
            {
                throw Failure(
                    TdeV2JitProvisioningStage.CapabilityCheck,
                    "The server capability for V2 TDE JIT provisioning could not be confirmed.",
                    ex);
            }

            if (!VaultCapabilityResolver.IsTdeV2JitProvisioningEnabled(config))
            {
                throw Failure(
                    TdeV2JitProvisioningStage.CapabilityCheck,
                    "The server has not explicitly enabled V2 TDE JIT provisioning.");
            }
        }
        _session.ThrowIfExpired(lease);

        TdeV2JitPendingStoreRecord? record = null;
        try
        {
            record = LoadOrCreatePending(
                binding,
                organizationId,
                organizationIdentifier,
                organizationPublicKeySpki.Span,
                trustCurrentDevice,
                mustResume);
            var registrationApi = _clients.GetAuthenticated<ITdeRegistrationApiClient>(lease);

            if (record.Stage == TdeV2JitPendingStage.Prepared)
            {
                AdvanceOrThrow(
                    ref record,
                    TdeV2JitPendingStage.AccountKeysSubmissionStarted,
                    TdeV2JitProvisioningStage.Preparation,
                    "The durable V2 account-key submission marker could not be stored.");
            }

            if (record!.Stage == TdeV2JitPendingStage.AccountKeysSubmissionStarted)
            {
                try
                {
                    var response = await registrationApi.PostAccountKeysAsync(
                        ToApiRequest(record.Pending.AccountKeysRequest),
                        linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    ValidatePostAccountKeysResponse(
                        response,
                        record.Pending.AccountKeysRequest);
                }
                catch (Exception ex) when (IsExpectedRemoteFailure(ex)
                    || ex is InvalidDataException or ArgumentException)
                {
                    throw Failure(
                        TdeV2JitProvisioningStage.AccountKeys,
                        "The V2 account keys could not be committed.",
                        ex);
                }

                AdvanceOrThrow(
                    ref record,
                    TdeV2JitPendingStage.AccountKeysConfirmed,
                    TdeV2JitProvisioningStage.AccountKeys,
                    "The confirmed V2 account-key state could not be stored.");
            }

            if (record!.Stage == TdeV2JitPendingStage.AccountKeysConfirmed)
            {
                try
                {
                    await registrationApi.EnrollResetPasswordAsync(
                        record.Pending.OrganizationId,
                        userId,
                        new ResetPasswordEnrollmentRequest(
                            record.Pending.ResetPasswordKey,
                            MasterPasswordHash: null),
                        linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                }
                catch (Exception ex) when (IsExpectedRemoteFailure(ex))
                {
                    throw Failure(
                        TdeV2JitProvisioningStage.ResetPasswordEnrollment,
                        "Account keys were committed, but account recovery enrollment failed.",
                        ex);
                }

                AdvanceOrThrow(
                    ref record,
                    TdeV2JitPendingStage.ResetPasswordEnrolled,
                    TdeV2JitProvisioningStage.ResetPasswordEnrollment,
                    "The confirmed account-recovery state could not be stored.");
            }

            if (record!.Stage == TdeV2JitPendingStage.ResetPasswordEnrolled)
            {
                if (record.Pending.TrustCurrentDevice)
                {
                    var request = record.Pending.TrustedDeviceRequest
                        ?? throw Failure(
                            TdeV2JitProvisioningStage.DeviceTrust,
                            "The saved trusted-device request is missing.");
                    var deviceApi = _clients.GetAuthenticated<IDeviceTrustApiClient>(lease);
                    DeviceDto? trustedDevice = null;
                    try
                    {
                        trustedDevice = await deviceApi.UpdateTrustedDeviceKeysAsync(
                            persisted.DeviceIdentifier,
                            new TrustedDeviceKeysRequest(
                                request.EncryptedUserKey,
                                request.EncryptedPublicKey,
                                request.EncryptedPrivateKey),
                            linked.Token).ConfigureAwait(false);
                        linked.Token.ThrowIfCancellationRequested();
                    }
                    catch (Exception ex) when (IsExpectedRemoteFailure(ex))
                    {
                        throw Failure(
                            TdeV2JitProvisioningStage.DeviceTrust,
                            "Account recovery was enrolled, but trusted-device enrollment failed.",
                            ex);
                    }

                    if (!trustedDevice.IsTrusted
                        || string.IsNullOrWhiteSpace(trustedDevice.Id)
                        || !string.Equals(
                            trustedDevice.Identifier,
                            persisted.DeviceIdentifier,
                            StringComparison.Ordinal))
                    {
                        await TryUntrustAsync(deviceApi, trustedDevice.Id).ConfigureAwait(false);
                        throw Failure(
                            TdeV2JitProvisioningStage.DeviceTrust,
                            "The server did not confirm V2 trusted-device enrollment.");
                    }

                    AdvanceOrThrow(
                        ref record,
                        TdeV2JitPendingStage.TrustedDeviceEnrolled,
                        TdeV2JitProvisioningStage.DeviceTrust,
                        "The confirmed trusted-device state could not be stored.");
                }
                else
                {
                    AdvanceOrThrow(
                        ref record,
                        TdeV2JitPendingStage.LocalCommitReady,
                        TdeV2JitProvisioningStage.LocalCommit,
                        "The completed remote enrollment state could not be stored.");
                }
            }

            if (record!.Stage == TdeV2JitPendingStage.TrustedDeviceEnrolled)
            {
                AdvanceOrThrow(
                    ref record,
                    TdeV2JitPendingStage.LocalCommitReady,
                    TdeV2JitProvisioningStage.LocalCommit,
                    "The completed remote enrollment state could not be stored.");
            }

            if (record!.Stage != TdeV2JitPendingStage.LocalCommitReady)
            {
                throw Failure(
                    TdeV2JitProvisioningStage.LocalCommit,
                    "The saved V2 TDE JIT state cannot be committed locally.");
            }

            var userKeyId = record.Pending.AccountKeysRequest.UserKeyId;
            var currentDeviceTrusted = record.Pending.TrustCurrentDevice;
            CommitLocalState(lease, binding, record.Pending);
            try
            {
                if (!_pendingStore.Remove(record, TdeV2JitPendingRemovalReason.Completed))
                {
                    throw new InvalidDataException(
                        "The completed V2 TDE JIT recovery record changed unexpectedly.");
                }
                record = null;
            }
            catch (Exception ex) when (IsExpectedStoreFailure(ex))
            {
                _session.TryLockIfCurrent(lease);
                throw Failure(
                    TdeV2JitProvisioningStage.LocalCommit,
                    "V2 TDE JIT provisioning was stored locally, but its recovery record could not be completed.",
                    ex);
            }

            try
            {
                await _bootstrapper.BootstrapAsync(lease, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpectedRemoteFailure(ex)
                || ex is CryptographicException or InvalidDataException)
            {
                _session.TryLockIfCurrent(lease);
                throw Failure(
                    TdeV2JitProvisioningStage.Bootstrap,
                    "V2 TDE JIT provisioning succeeded, but the vault could not be loaded.",
                    ex);
            }

            return new TdeV2JitProvisioningResult(
                lease,
                userKeyId,
                currentDeviceTrusted);
        }
        finally
        {
            record?.Dispose();
        }
    }

    private TdeV2JitPendingStoreRecord LoadOrCreatePending(
        DeviceKeyBinding binding,
        string organizationId,
        string organizationIdentifier,
        ReadOnlySpan<byte> organizationPublicKeySpki,
        bool trustCurrentDevice,
        bool mustResume)
    {
        var existing = ReadPending(binding, TdeV2JitProvisioningStage.Preparation);
        if (existing is not null)
        {
            try
            {
                EnsurePendingContext(
                    existing,
                    organizationId,
                    organizationIdentifier,
                    trustCurrentDevice);
                return existing;
            }
            catch
            {
                existing.Dispose();
                throw;
            }
        }

        if (mustResume)
        {
            throw Failure(
                TdeV2JitProvisioningStage.Preparation,
                "The saved V2 TDE JIT enrollment changed before it could be resumed.");
        }
        if (organizationPublicKeySpki.IsEmpty)
        {
            throw Failure(
                TdeV2JitProvisioningStage.Preparation,
                "The organization public recovery key is required to prepare V2 TDE JIT enrollment.");
        }

        byte[]? userKeyTransport = null;
        byte[]? deviceKey = null;
        using var material = CreateMaterial(organizationPublicKeySpki, trustCurrentDevice);
        try
        {
            var accountKeys = material.AccountKeys;
            userKeyTransport = accountKeys.UserKey.ExportTransport();
            var trusted = material.TrustedDeviceKeys;
            var trustedDeviceRequest = trusted is null
                ? null
                : new TdeV2JitTrustedDeviceRequestSnapshot(
                    trusted.EncryptedUserKey,
                    trusted.EncryptedPublicKey,
                    trusted.EncryptedPrivateKey);
            if (trusted is not null)
                deviceKey = trusted.ExportDeviceKey();

            var accountKeysSnapshot = new TdeV2JitAccountKeysSnapshot(
                accountKeys.WrappedPrivateKey,
                accountKeys.AccountPublicKey,
                new TdeV2JitPublicKeyEncryptionKeyPairSnapshot(
                    accountKeys.WrappedPrivateKey,
                    accountKeys.AccountPublicKey,
                    accountKeys.SignedPublicKey),
                new TdeV2JitSignatureKeyPairSnapshot(
                    accountKeys.SignatureAlgorithm,
                    accountKeys.WrappedSigningKey,
                    accountKeys.VerifyingKey),
                new TdeV2JitAccountSecurityStateSnapshot(
                    accountKeys.SecurityState,
                    accountKeys.SecurityVersion));
            var requestSnapshot = new TdeV2JitAccountKeysRequestSnapshot(
                accountKeys.AccountPublicKey,
                accountKeys.WrappedPrivateKey,
                accountKeysSnapshot,
                accountKeys.UserKey.KeyIdHex);
            using var pending = new TdeV2JitPending(
                binding,
                organizationId,
                organizationIdentifier,
                DateTimeOffset.UtcNow,
                userKeyTransport,
                requestSnapshot,
                material.ResetPasswordKey,
                trustedDeviceRequest,
                deviceKey is null ? ReadOnlySpan<byte>.Empty : deviceKey);
            var created = _pendingStore.TryCreate(pending);
            if (created is not null)
                return created;

            var raced = ReadPending(binding, TdeV2JitProvisioningStage.Preparation)
                ?? throw Failure(
                    TdeV2JitProvisioningStage.Preparation,
                    "Another V2 TDE JIT enrollment changed before it could be recovered.");
            try
            {
                EnsurePendingContext(
                    raced,
                    organizationId,
                    organizationIdentifier,
                    trustCurrentDevice);
                return raced;
            }
            catch
            {
                raced.Dispose();
                throw;
            }
        }
        catch (TdeV2JitProvisioningException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedStoreFailure(ex))
        {
            throw Failure(
                TdeV2JitProvisioningStage.Preparation,
                "The V2 TDE JIT recovery material could not be stored before submission.",
                ex);
        }
        finally
        {
            if (deviceKey is not null)
                CryptographicOperations.ZeroMemory(deviceKey);
            if (userKeyTransport is not null)
                CryptographicOperations.ZeroMemory(userKeyTransport);
        }
    }

    private TdeV2JitPendingStoreRecord? ReadPending(
        DeviceKeyBinding binding,
        TdeV2JitProvisioningStage failureStage)
    {
        try
        {
            var read = _pendingStore.Read(binding);
            return read.Status switch
            {
                TdeV2JitPendingReadStatus.Absent => null,
                TdeV2JitPendingReadStatus.Found when read.Record is not null => read.Record,
                TdeV2JitPendingReadStatus.Corrupt => throw new InvalidDataException(
                    "The saved V2 TDE JIT recovery record is corrupt."),
                TdeV2JitPendingReadStatus.Unavailable => throw new IOException(
                    "The V2 TDE JIT recovery store is unavailable."),
                _ => throw new InvalidDataException(
                    "The V2 TDE JIT recovery store returned an invalid result."),
            };
        }
        catch (Exception ex) when (IsExpectedStoreFailure(ex))
        {
            throw Failure(
                failureStage,
                "The saved V2 TDE JIT recovery state is unavailable.",
                ex);
        }
    }

    private bool PendingExists(DeviceKeyBinding binding)
    {
        var record = ReadPending(binding, TdeV2JitProvisioningStage.Preparation);
        if (record is null)
            return false;
        record.Dispose();
        return true;
    }

    private static void EnsurePendingContext(
        TdeV2JitPendingStoreRecord record,
        string organizationId,
        string organizationIdentifier,
        bool trustCurrentDevice)
    {
        var pending = record.Pending;
        if (!string.Equals(pending.OrganizationId, organizationId, StringComparison.Ordinal)
            || !string.Equals(
                pending.OrganizationIdentifier,
                organizationIdentifier,
                StringComparison.Ordinal)
            || pending.TrustCurrentDevice != trustCurrentDevice)
        {
            throw Failure(
                PendingFailureStage(record.Stage),
                "A different V2 TDE JIT enrollment is already pending for this account and device.");
        }
    }

    private void AdvanceOrThrow(
        ref TdeV2JitPendingStoreRecord? record,
        TdeV2JitPendingStage nextStage,
        TdeV2JitProvisioningStage failureStage,
        string message)
    {
        var current = record
            ?? throw new InvalidOperationException("A V2 TDE JIT recovery record is required.");
        try
        {
            var next = _pendingStore.TryAdvance(current, nextStage);
            if (next is null)
                throw new InvalidDataException(
                    "The V2 TDE JIT recovery record changed unexpectedly.");
            record = next;
        }
        catch (Exception ex) when (IsExpectedStoreFailure(ex))
        {
            throw Failure(failureStage, message, ex);
        }
    }

    private void CommitLocalState(
        VaultSessionLease lease,
        DeviceKeyBinding binding,
        TdeV2JitPending pending)
    {
        V2UserKey? sessionUserKey = null;
        byte[]? userKeyTransport = null;
        byte[]? deviceKey = null;
        var transferred = false;
        try
        {
            userKeyTransport = pending.CopyUserKeyTransport();
            sessionUserKey = V2UserKey.ImportTransport(userKeyTransport);
            deviceKey = pending.CopyDeviceKey();
            var accountKeys = pending.AccountKeysRequest.AccountKeys;
            var accountCryptographicState = new PersistedAccountCryptographicState(
                accountKeys.SecurityState.SecurityVersion,
                accountKeys.PublicKeyEncryptionKeyPair.WrappedPrivateKey,
                accountKeys.PublicKeyEncryptionKeyPair.PublicKey,
                accountKeys.PublicKeyEncryptionKeyPair.SignedPublicKey,
                accountKeys.SignatureKeyPair.WrappedSigningKey,
                accountKeys.SignatureKeyPair.VerifyingKey,
                accountKeys.SecurityState.SecurityState);
            var committed = _session.TryCommit(lease, () =>
            {
                if (!_tokenStore.TryLoad(out var latestPersisted)
                    || !_session.IsCurrent(lease, latestPersisted))
                {
                    throw new InvalidDataException(
                        "The persisted session changed during V2 TDE JIT provisioning.");
                }
                if (deviceKey is not null)
                    _deviceKeys.Save(binding, deviceKey);
                _tokenStore.Save(latestPersisted with
                {
                    Generation = lease.Generation,
                    UnlockMethod = PersistedSessionUnlockMethod.TrustedDeviceSso,
                    KeyConnectorUrl = null,
                    SupportsDeviceTrust = true,
                    UserKeySecurityVersion = 2,
                    UserKeyId = pending.AccountKeysRequest.UserKeyId,
                    AccountCryptographicState = accountCryptographicState,
                });
                _session.SetUnlockedKey(sessionUserKey);
                transferred = true;
                _session.SetEncryptedPrivateKey(
                    pending.AccountKeysRequest.EncryptedPrivateKey);
                _deviceTrust.SetCurrentSessionCapability(lease, supported: true);
            });
            if (!committed)
                throw new OperationCanceledException(lease.CancellationToken);
        }
        catch (Exception ex) when (IsExpectedStoreFailure(ex)
            || ex is OperationCanceledException)
        {
            _session.TryLockIfCurrent(lease);
            throw Failure(
                TdeV2JitProvisioningStage.LocalCommit,
                "V2 TDE JIT provisioning completed remotely but could not be stored locally.",
                ex);
        }
        finally
        {
            if (!transferred)
                sessionUserKey?.Dispose();
            if (deviceKey is not null)
                CryptographicOperations.ZeroMemory(deviceKey);
            if (userKeyTransport is not null)
                CryptographicOperations.ZeroMemory(userKeyTransport);
        }
    }

    private static PostAccountKeysRequest ToApiRequest(
        TdeV2JitAccountKeysRequestSnapshot snapshot)
    {
        var accountKeys = snapshot.AccountKeys;
        return new PostAccountKeysRequest(
            snapshot.PublicKey,
            snapshot.EncryptedPrivateKey,
            new AccountKeysRequest(
                accountKeys.UserKeyEncryptedAccountPrivateKey,
                accountKeys.AccountPublicKey,
                new PublicKeyEncryptionKeyPairRequest(
                    accountKeys.PublicKeyEncryptionKeyPair.WrappedPrivateKey,
                    accountKeys.PublicKeyEncryptionKeyPair.PublicKey,
                    accountKeys.PublicKeyEncryptionKeyPair.SignedPublicKey),
                new SignatureKeyPairRequest(
                    accountKeys.SignatureKeyPair.SignatureAlgorithm,
                    accountKeys.SignatureKeyPair.WrappedSigningKey,
                    accountKeys.SignatureKeyPair.VerifyingKey),
                new AccountSecurityState(
                    accountKeys.SecurityState.SecurityState,
                    accountKeys.SecurityState.SecurityVersion)),
            snapshot.UserKeyId);
    }

    private static void ValidatePostAccountKeysResponse(
        PostAccountKeysResponse response,
        TdeV2JitAccountKeysRequestSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.PublicKey is not null
            && !string.Equals(response.PublicKey, snapshot.PublicKey, StringComparison.Ordinal)
            || response.PrivateKey is not null
            && !string.Equals(
                response.PrivateKey,
                snapshot.EncryptedPrivateKey,
                StringComparison.Ordinal)
            || response.AccountKeys is not null
            && !AccountKeysMatch(response.AccountKeys, snapshot))
        {
            throw new InvalidDataException(
                "The server returned account keys that do not match the submitted request.");
        }
    }

    internal static bool AccountKeysMatch(
        AccountKeysResponse response,
        TdeV2JitAccountKeysRequestSnapshot snapshot)
    {
        var expected = snapshot.AccountKeys;
        var publicPair = response.PublicKeyEncryptionKeyPair;
        var signaturePair = response.SignatureKeyPair;
        var securityState = response.SecurityState;
        return publicPair is not null
            && signaturePair is not null
            && securityState is not null
            && string.Equals(
                publicPair.WrappedPrivateKey,
                expected.PublicKeyEncryptionKeyPair.WrappedPrivateKey,
                StringComparison.Ordinal)
            && (publicPair.PublicKey is null
                || string.Equals(
                    publicPair.PublicKey,
                    expected.PublicKeyEncryptionKeyPair.PublicKey,
                    StringComparison.Ordinal))
            && (publicPair.SignedPublicKey is null
                || string.Equals(
                    publicPair.SignedPublicKey,
                    expected.PublicKeyEncryptionKeyPair.SignedPublicKey,
                    StringComparison.Ordinal))
            && string.Equals(
                signaturePair.WrappedSigningKey,
                expected.SignatureKeyPair.WrappedSigningKey,
                StringComparison.Ordinal)
            && (signaturePair.VerifyingKey is null
                || string.Equals(
                    signaturePair.VerifyingKey,
                    expected.SignatureKeyPair.VerifyingKey,
                    StringComparison.Ordinal))
            && string.Equals(
                securityState.SecurityState,
                expected.SecurityState.SecurityState,
                StringComparison.Ordinal)
            && securityState.SecurityVersion == expected.SecurityState.SecurityVersion;
    }

    private static TdeV2JitProvisioningStage PendingFailureStage(
        TdeV2JitPendingStage stage) =>
        stage >= TdeV2JitPendingStage.AccountKeysSubmissionStarted
            ? TdeV2JitProvisioningStage.AccountKeys
            : TdeV2JitProvisioningStage.Preparation;

    private static V2TdeRegistrationMaterial CreateMaterial(
        ReadOnlySpan<byte> organizationPublicKeySpki,
        bool trustCurrentDevice)
    {
        try
        {
            return V2TdeRegistrationMaterial.Create(
                organizationPublicKeySpki,
                trustCurrentDevice);
        }
        catch (Exception ex) when (ex is
            CryptographicException or ArgumentException or PlatformNotSupportedException)
        {
            throw Failure(
                TdeV2JitProvisioningStage.Preparation,
                "V2 TDE account material could not be generated.",
                ex);
        }
    }

    private static async Task TryUntrustAsync(
        IDeviceTrustApiClient api,
        string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;
        try
        {
            await api.UntrustDevicesAsync(
                new UntrustDevicesRequest([deviceId]),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedRemoteFailure(ex))
        {
        }
    }

    private static string RequireGuid(string value, string parameterName) =>
        Guid.TryParseExact(value, "D", out var parsed)
            ? parsed.ToString("D")
            : throw new ArgumentException("The identifier must be a canonical GUID.", parameterName);

    private static bool IsExpectedRemoteFailure(Exception ex) => ex is
        HttpRequestException
            or VaultApiException
            or VaultWriteException
            or OperationCanceledException;

    private static bool IsExpectedStoreFailure(Exception ex) => ex is
        IOException
            or UnauthorizedAccessException
            or CryptographicException
            or InvalidDataException
            or System.Text.Json.JsonException
            or ArgumentException
            or PlatformNotSupportedException;

    private static TdeV2JitProvisioningException Failure(
        TdeV2JitProvisioningStage stage,
        string message,
        Exception? inner = null) =>
        new(stage, message, inner);

    private sealed record PendingResumeContext(
        string OrganizationId,
        string OrganizationIdentifier,
        bool TrustCurrentDevice);
}
