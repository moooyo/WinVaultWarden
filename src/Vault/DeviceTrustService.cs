using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Crypto.V2;

namespace Vault;

public sealed class DeviceTrustService : IDeviceTrustService
{
    private const int MaximumEncryptedValueLength = 16 * 1024;

    private readonly object _capabilityGate = new();
    private readonly object _deviceKeyStateGate = new();
    private readonly SemaphoreSlim _enrollmentGate = new(1, 1);
    private readonly IVaultApiClientProvider _clients;
    private readonly TrustedDeviceCrypto _crypto;
    private readonly IDeviceKeyStore _deviceKeys;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;
    private readonly IDeviceTrustCompensationClient? _compensation;
    // Remove(false) cannot prove absence, so these bindings remain unusable
    // until a complete enrollment safely overwrites their stored key.
    private readonly HashSet<DeviceKeyBinding> _unusableDeviceKeys = [];
    private CapabilityBinding? _capability;

    public DeviceTrustService(
        IVaultApiClientProvider clients,
        TrustedDeviceCrypto crypto,
        IDeviceKeyStore deviceKeys,
        VaultSession session,
        ITokenStore tokenStore,
        IDeviceTrustCompensationClient? compensation = null)
    {
        _clients = clients;
        _crypto = crypto;
        _deviceKeys = deviceKeys;
        _session = session;
        _tokenStore = tokenStore;
        _compensation = compensation;
    }

    public bool CanTrustCurrentDevice
    {
        get
        {
            return TryGetCurrentBinding(out var binding, out var lease)
                && CanTrustDevice(lease, binding);
        }
    }

    public bool IsCurrentDeviceTrusted =>
        TryGetCurrentBinding(out var binding, out _) && HasStoredDeviceKey(binding);

    public void SetCurrentSessionCapability(VaultSessionLease lease, bool supported)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!_session.IsCurrent(lease))
            return;

        lock (_capabilityGate)
            _capability = new CapabilityBinding(
                lease,
                supported);
    }

    public void RecordTrustLoss(
        string accountId,
        ServerEndpoints endpoints,
        string deviceIdentifier)
    {
        DeviceKeyBinding binding;
        try
        {
            binding = new DeviceKeyBinding(accountId, endpoints, deviceIdentifier);
        }
        catch (ArgumentException ex)
        {
            throw new DeviceTrustException(
                "The trusted-device binding is invalid.",
                ex);
        }

        RemoveOrQuarantineDeviceKey(binding);
    }

    public bool TryDecryptUserKey(
        string accountId,
        ServerEndpoints endpoints,
        string deviceIdentifier,
        string encryptedPrivateKey,
        string encryptedUserKey,
        out byte[] userKey)
    {
        userKey = [];
        DeviceKeyBinding binding;
        try
        {
            binding = new DeviceKeyBinding(accountId, endpoints, deviceIdentifier);
        }
        catch (ArgumentException ex)
        {
            throw new DeviceTrustException(
                "The trusted-device binding is invalid.",
                ex);
        }
        lock (_deviceKeyStateGate)
        {
            if (_unusableDeviceKeys.Contains(binding)
                || !_deviceKeys.TryLoad(binding, out var deviceKeyBytes))
            {
                return false;
            }

            try
            {
                if (!IsBoundedEncryptedValue(encryptedPrivateKey)
                    || !IsBoundedEncryptedValue(encryptedUserKey))
                {
                    throw new CryptographicException(
                        "Trusted device cryptographic material is invalid.");
                }

                using var deviceKey = new SymmetricCryptoKey(deviceKeyBytes);
                userKey = _crypto.DecryptUserKeyTransport(
                    deviceKey,
                    EncString.Parse(encryptedPrivateKey),
                    EncString.Parse(encryptedUserKey));
                return true;
            }
            catch (Exception ex) when (
                ex is CryptographicException or FormatException or ArgumentException)
            {
                RemoveOrQuarantineDeviceKeyNoLock(binding);
                throw new DeviceTrustException(
                    "The local trusted-device key is no longer usable.",
                    ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(deviceKeyBytes);
            }
        }
    }

    public async Task TrustCurrentDeviceAsync(CancellationToken ct = default)
    {
        await _enrollmentGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await TrustCurrentDeviceCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _enrollmentGate.Release();
        }
    }

    private async Task TrustCurrentDeviceCoreAsync(CancellationToken ct)
    {
        if (!TryGetCurrentBinding(out var binding, out var lease)
            || !CanTrustDevice(lease, binding)
            || !_tokenStore.TryLoad(out var persisted)
            || !_session.IsCurrent(lease, persisted)
            || !_session.TryCopyUserKeyMaterial(lease, out var copiedUserKey))
        {
            throw new DeviceTrustException(
                "Trusted-device encryption is not available for this session.");
        }
        var frozenAuthorization = _session.GetAccessTokenBinding(lease)
            ?? throw new DeviceTrustException(
                "The authenticated device session is unavailable.");

        PreparedTrustedDeviceMaterial material;
        try
        {
            using (copiedUserKey)
                material = PrepareTrustMaterial(copiedUserKey!);
        }
        catch (Exception ex) when (ex is
            CryptographicException
                or FormatException
                or ArgumentException
                or PlatformNotSupportedException)
        {
            throw new DeviceTrustException(
                "Trusted-device cryptographic material could not be created.",
                ex);
        }
        using (material)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, lease.CancellationToken);
            var api = _clients.GetAuthenticated<IDeviceTrustApiClient>(lease);
            var response = await api.UpdateTrustedDeviceKeysAsync(
                persisted.DeviceIdentifier,
                new TrustedDeviceKeysRequest(
                    material.EncryptedUserKey.ToString(),
                    material.EncryptedPublicKey,
                    material.EncryptedPrivateKey),
                linked.Token).ConfigureAwait(false);
            var compensationAuthorization =
                _session.GetAccessTokenBinding(lease) ?? frozenAuthorization;

            if (!response.IsTrusted
                || string.IsNullOrWhiteSpace(response.Id)
                || !string.Equals(
                    response.Identifier,
                    persisted.DeviceIdentifier,
                    StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(response.Id))
                    await TryCompensateUntrustAsync(
                        compensationAuthorization,
                        api,
                        response.Id).ConfigureAwait(false);
                throw new DeviceTrustException(
                    "The server did not confirm trusted-device enrollment.");
            }

            if (linked.IsCancellationRequested)
            {
                await TryCompensateUntrustAsync(
                    compensationAuthorization,
                    api,
                    response.Id).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
            }

            var localPersistenceStarted = false;
            try
            {
                var committed = _session.TryCommit(lease, () =>
                {
                    localPersistenceStarted = true;
                    if (!_tokenStore.TryLoad(out var latestPersisted)
                        || !_session.IsCurrent(lease, latestPersisted))
                    {
                        throw new InvalidDataException(
                            "The persisted session changed during trusted-device enrollment.");
                    }
                    PersistQuarantinedEnrollmentState(
                        binding,
                        material.DeviceKey,
                        latestPersisted with
                        {
                            Generation = lease.Generation,
                            UnlockMethod = PersistedSessionUnlockMethod.TrustedDeviceSso,
                            KeyConnectorUrl = null,
                            SupportsDeviceTrust = true,
                        });
                });
                if (!committed)
                    throw new OperationCanceledException(lease.CancellationToken);
                MarkDeviceKeyUsable(binding);
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or CryptographicException
                    or InvalidDataException
                    or System.Text.Json.JsonException
                    or ArgumentException
                    or PlatformNotSupportedException
                    or OperationCanceledException)
            {
                if (localPersistenceStarted)
                    RemoveOrQuarantineDeviceKey(binding);
                await TryCompensateUntrustAsync(
                    compensationAuthorization,
                    api,
                    response.Id).ConfigureAwait(false);
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                    throw;
                throw new DeviceTrustException(
                    "Trusted-device enrollment could not be stored securely.",
                    ex);
            }
        }
    }

    private bool CanTrustDevice(
        VaultSessionLease lease,
        DeviceKeyBinding binding)
    {
        if (!_session.IsUnlocked(lease))
            return false;

        lock (_capabilityGate)
        {
            return _capability is { Supported: true } capability
                && capability.Matches(lease)
                && !HasStoredDeviceKey(binding);
        }
    }

    private PreparedTrustedDeviceMaterial PrepareTrustMaterial(VaultUserKey userKey)
    {
        if (userKey.IsV2)
        {
            using var material = V2TrustedDeviceKeyMaterial.Create(userKey.V2!);
            return new PreparedTrustedDeviceMaterial(
                material.ExportDeviceKey(),
                material.EncryptedUserKey,
                material.EncryptedPublicKey,
                material.EncryptedPrivateKey);
        }

        using var legacy = _crypto.CreateTrustMaterial(userKey.Legacy!);
        return new PreparedTrustedDeviceMaterial(
            legacy.DeviceKey.FullKey.ToArray(),
            legacy.EncryptedUserKey.ToString(),
            legacy.EncryptedPublicKey.ToString(),
            legacy.EncryptedPrivateKey.ToString());
    }

    private bool TryGetCurrentBinding(
        out DeviceKeyBinding binding,
        out VaultSessionLease lease)
    {
        binding = null!;
        lease = null!;
        try
        {
            lease = _session.CaptureLease();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (!_tokenStore.TryLoad(out var persisted)
            || !_session.IsCurrent(lease, persisted)
            || persisted.Endpoints is null)
        {
            return false;
        }

        try
        {
            binding = new DeviceKeyBinding(
                persisted.AccountId,
                persisted.Endpoints,
                persisted.DeviceIdentifier);
            return true;
        }
        catch (ArgumentException)
        {
            binding = null!;
            return false;
        }
    }

    private bool HasStoredDeviceKey(DeviceKeyBinding binding)
    {
        lock (_deviceKeyStateGate)
        {
            if (_unusableDeviceKeys.Contains(binding)
                || !_deviceKeys.TryLoad(binding, out var key))
            {
                return false;
            }

            CryptographicOperations.ZeroMemory(key);
            return true;
        }
    }

    private void PersistQuarantinedEnrollmentState(
        DeviceKeyBinding binding,
        ReadOnlySpan<byte> deviceKey,
        PersistedSession persisted)
    {
        lock (_deviceKeyStateGate)
        {
            _unusableDeviceKeys.Add(binding);
            _deviceKeys.Save(binding, deviceKey);
        }
        _tokenStore.Save(persisted);
    }

    private void MarkDeviceKeyUsable(DeviceKeyBinding binding)
    {
        lock (_deviceKeyStateGate)
            _unusableDeviceKeys.Remove(binding);
    }

    private void RemoveOrQuarantineDeviceKey(DeviceKeyBinding binding)
    {
        lock (_deviceKeyStateGate)
            RemoveOrQuarantineDeviceKeyNoLock(binding);
    }

    private void RemoveOrQuarantineDeviceKeyNoLock(DeviceKeyBinding binding)
    {
        _unusableDeviceKeys.Add(binding);
        try
        {
            if (_deviceKeys.Remove(binding))
                _unusableDeviceKeys.Remove(binding);
        }
        catch (Exception ex) when (ex is
            IOException
                or UnauthorizedAccessException
                or CryptographicException
                or InvalidDataException
                or ArgumentException
                or PlatformNotSupportedException)
        {
        }
    }

    private async Task TryCompensateUntrustAsync(
        AccessTokenBinding authorization,
        IDeviceTrustApiClient api,
        string deviceId)
    {
        try
        {
            if (_compensation is not null)
            {
                await _compensation.UntrustAsync(
                    authorization,
                    deviceId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await api.UntrustDevicesAsync(
                    new UntrustDevicesRequest([deviceId]),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or VaultApiException
                or OperationCanceledException)
        {
        }
    }

    private static bool IsBoundedEncryptedValue(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumEncryptedValueLength;

    private sealed record CapabilityBinding(
        VaultSessionLease Lease,
        bool Supported)
    {
        public bool Matches(VaultSessionLease lease) =>
            Lease.SameSessionAs(lease);
    }

    private sealed class PreparedTrustedDeviceMaterial(
        byte[] deviceKey,
        string encryptedUserKey,
        string encryptedPublicKey,
        string encryptedPrivateKey) : IDisposable
    {
        public byte[] DeviceKey { get; } = deviceKey;
        public string EncryptedUserKey { get; } = encryptedUserKey;
        public string EncryptedPublicKey { get; } = encryptedPublicKey;
        public string EncryptedPrivateKey { get; } = encryptedPrivateKey;

        public void Dispose() => CryptographicOperations.ZeroMemory(DeviceKey);
    }
}
