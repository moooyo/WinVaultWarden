using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Crypto.V2;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class DeviceTrustServiceTests
{
    private const string AccountId = "account-id";
    private const string DeviceIdentifier = "11111111-2222-3333-4444-555555555555";
    private static readonly ServerEndpoints Endpoints =
        ServerEndpoints.FromWebVault("https://vault.example");

    [Fact]
    public async Task TrustCurrentDevice_UploadsThenPersistsBoundDeviceKey()
    {
        var session = CreateUnlockedSession(out var expectedUserKey);
        var persistedStore = CreatePersistedStore(session);
        var deviceKeyStore = new MemoryDeviceKeyStore();
        var api = new FakeDeviceTrustApiClient();
        var service = CreateService(
            session,
            persistedStore,
            deviceKeyStore,
            api);
        var lease = session.CaptureLease();
        service.SetCurrentSessionCapability(lease, supported: true);

        Assert.True(service.CanTrustCurrentDevice);
        await service.TrustCurrentDeviceAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(api.LastKeys);
        Assert.StartsWith("4.", api.LastKeys!.EncryptedUserKey);
        Assert.StartsWith("2.", api.LastKeys.EncryptedPublicKey);
        Assert.StartsWith("2.", api.LastKeys.EncryptedPrivateKey);
        Assert.True(service.IsCurrentDeviceTrusted);
        Assert.False(service.CanTrustCurrentDevice);
        Assert.True(persistedStore.TryLoad(out var persisted));
        Assert.Equal(PersistedSessionUnlockMethod.TrustedDeviceSso, persisted.UnlockMethod);

        Assert.True(service.TryDecryptUserKey(
            AccountId,
            Endpoints,
            DeviceIdentifier,
            api.LastKeys.EncryptedPrivateKey,
            api.LastKeys.EncryptedUserKey,
            out var decrypted));
        try
        {
            Assert.Equal(expectedUserKey, decrypted);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(decrypted);
        }
    }

    [Fact]
    public async Task TrustCurrentDevice_V2UploadsOfficialMixedKeyGraphAndRecoversCoseUserKey()
    {
        var session = new VaultSession();
        session.BeginAccountSession(AccountId, "me@example.com", Endpoints);
        session.SetTokens("access-token", "refresh-token");
        var expectedKeyId = string.Empty;
        var v2UserKey = V2UserKey.Generate();
        expectedKeyId = v2UserKey.KeyIdHex;
        session.SetUnlockedKey(v2UserKey);
        var persistedStore = CreatePersistedStore(session);
        var deviceKeyStore = new MemoryDeviceKeyStore();
        var api = new FakeDeviceTrustApiClient();
        var service = CreateService(
            session,
            persistedStore,
            deviceKeyStore,
            api);
        service.SetCurrentSessionCapability(session.CaptureLease(), supported: true);

        await service.TrustCurrentDeviceAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("4.", api.LastKeys!.EncryptedUserKey);
        Assert.StartsWith("7.", api.LastKeys.EncryptedPublicKey);
        Assert.StartsWith("2.", api.LastKeys.EncryptedPrivateKey);
        Assert.True(service.TryDecryptUserKey(
            AccountId,
            Endpoints,
            DeviceIdentifier,
            api.LastKeys.EncryptedPrivateKey,
            api.LastKeys.EncryptedUserKey,
            out var transport));
        try
        {
            using var recovered = V2UserKey.ImportTransport(transport);
            Assert.Equal(expectedKeyId, recovered.KeyIdHex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transport);
        }
    }

    [Fact]
    public async Task RecordTrustLoss_RemoveFailure_QuarantinesAndAllowsEnrollmentOverwrite()
    {
        var session = CreateUnlockedSession(out var expectedUserKey);
        var persistedStore = CreatePersistedStore(session);
        var deviceKeyStore = new MemoryDeviceKeyStore { RemoveSucceeds = false };
        var api = new FakeDeviceTrustApiClient();
        var service = CreateService(
            session,
            persistedStore,
            deviceKeyStore,
            api);
        service.SetCurrentSessionCapability(session.CaptureLease(), supported: true);
        var staleDeviceKey = Enumerable.Range(193, IDeviceKeyStore.KeyLength)
            .Select(value => (byte)value)
            .ToArray();
        try
        {
            deviceKeyStore.Save(CurrentBinding(), staleDeviceKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(staleDeviceKey);
        }

        Assert.True(service.IsCurrentDeviceTrusted);
        Assert.False(service.CanTrustCurrentDevice);

        service.RecordTrustLoss(AccountId, Endpoints, DeviceIdentifier);

        Assert.Equal(1, deviceKeyStore.RemoveCalls);
        Assert.True(deviceKeyStore.Contains(CurrentBinding()));
        Assert.False(service.IsCurrentDeviceTrusted);
        Assert.True(service.CanTrustCurrentDevice);

        await service.TrustCurrentDeviceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, api.UpdateCalls);
        Assert.Equal(2, deviceKeyStore.SaveCalls);
        Assert.True(service.IsCurrentDeviceTrusted);
        Assert.False(service.CanTrustCurrentDevice);
        Assert.True(service.TryDecryptUserKey(
            AccountId,
            Endpoints,
            DeviceIdentifier,
            api.LastKeys!.EncryptedPrivateKey,
            api.LastKeys.EncryptedUserKey,
            out var recoveredUserKey));
        try
        {
            Assert.Equal(expectedUserKey, recoveredUserKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveredUserKey);
        }
    }

    [Fact]
    public async Task TryDecryptUserKey_BadKeyAndRemoveFailure_QuarantinesAndCanBeOverwritten()
    {
        var session = CreateUnlockedSession(out var expectedUserKey);
        var persistedStore = CreatePersistedStore(session);
        var deviceKeyStore = new MemoryDeviceKeyStore { RemoveSucceeds = false };
        var api = new FakeDeviceTrustApiClient();
        var service = CreateService(
            session,
            persistedStore,
            deviceKeyStore,
            api);
        service.SetCurrentSessionCapability(session.CaptureLease(), supported: true);

        using var userKey = new SymmetricCryptoKey(expectedUserKey.ToArray());
        using var staleMaterial = new TrustedDeviceCrypto(new CryptoService())
            .CreateTrustMaterial(userKey);
        var wrongDeviceKey = Enumerable.Range(129, IDeviceKeyStore.KeyLength)
            .Select(value => (byte)value)
            .ToArray();
        try
        {
            deviceKeyStore.Save(CurrentBinding(), wrongDeviceKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrongDeviceKey);
        }

        byte[] failedUserKey = [0xA5];
        Assert.Throws<DeviceTrustException>(() => service.TryDecryptUserKey(
            AccountId,
            Endpoints,
            DeviceIdentifier,
            staleMaterial.EncryptedPrivateKey.ToString(),
            staleMaterial.EncryptedUserKey.ToString(),
            out failedUserKey));

        Assert.Empty(failedUserKey);
        Assert.Equal(1, deviceKeyStore.RemoveCalls);
        Assert.True(deviceKeyStore.Contains(CurrentBinding()));
        Assert.False(service.IsCurrentDeviceTrusted);
        Assert.True(service.CanTrustCurrentDevice);
        Assert.Equal(1, deviceKeyStore.TryLoadCalls);
        Assert.False(service.TryDecryptUserKey(
            AccountId,
            Endpoints,
            DeviceIdentifier,
            staleMaterial.EncryptedPrivateKey.ToString(),
            staleMaterial.EncryptedUserKey.ToString(),
            out var retryUserKey));
        Assert.Empty(retryUserKey);
        Assert.Equal(1, deviceKeyStore.TryLoadCalls);

        await service.TrustCurrentDeviceAsync(TestContext.Current.CancellationToken);

        Assert.True(service.IsCurrentDeviceTrusted);
        Assert.False(service.CanTrustCurrentDevice);
        Assert.Equal(2, deviceKeyStore.SaveCalls);
        Assert.True(service.TryDecryptUserKey(
            AccountId,
            Endpoints,
            DeviceIdentifier,
            api.LastKeys!.EncryptedPrivateKey,
            api.LastKeys.EncryptedUserKey,
            out var recoveredUserKey));
        try
        {
            Assert.Equal(expectedUserKey, recoveredUserKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveredUserKey);
        }
    }

    [Fact]
    public async Task TrustCurrentDevice_LocalPersistenceFailure_UntrustsServerDevice()
    {
        var session = CreateUnlockedSession(out _);
        var persistedStore = CreatePersistedStore(session);
        var deviceKeyStore = new MemoryDeviceKeyStore { ThrowOnSave = true };
        var api = new FakeDeviceTrustApiClient();
        var service = CreateService(
            session,
            persistedStore,
            deviceKeyStore,
            api);
        service.SetCurrentSessionCapability(session.CaptureLease(), supported: true);

        await Assert.ThrowsAsync<DeviceTrustException>(
            () => service.TrustCurrentDeviceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["server-device-id"], api.LastUntrustedDeviceIds);
        Assert.False(service.IsCurrentDeviceTrusted);
        Assert.True(persistedStore.TryLoad(out var persisted));
        Assert.Equal(PersistedSessionUnlockMethod.MasterPassword, persisted.UnlockMethod);
    }

    [Fact]
    public async Task TrustCurrentDevice_TokenRefreshThenLocalFailure_UsesLatestCompensationBinding()
    {
        var session = CreateUnlockedSession(out _);
        var lease = session.CaptureLease();
        var persistedStore = CreatePersistedStore(session);
        var api = new FakeDeviceTrustApiClient
        {
            AfterUpdate = () => session.SetTokens(
                "refreshed-access-token",
                "refreshed-refresh-token"),
        };
        var compensation = new FakeCompensationClient();
        var service = CreateService(
            session,
            persistedStore,
            new MemoryDeviceKeyStore { ThrowOnSave = true },
            api,
            compensation);
        service.SetCurrentSessionCapability(lease, supported: true);

        await Assert.ThrowsAsync<DeviceTrustException>(
            () => service.TrustCurrentDeviceAsync(TestContext.Current.CancellationToken));

        Assert.Equal("refreshed-access-token", compensation.Binding!.Token);
        Assert.Equal(lease.Generation, compensation.Binding.Generation);
        Assert.Equal("server-device-id", compensation.DeviceId);
    }

    [Fact]
    public async Task TrustCurrentDevice_TokenPersistenceAndRemoveFailure_AllowsRetry()
    {
        var session = CreateUnlockedSession(out _);
        var persistedStore = CreatePersistedStore(session);
        var tokenStore = new SwitchableTokenStore(persistedStore)
        {
            ThrowOnSave = true,
        };
        var deviceKeyStore = new MemoryDeviceKeyStore { RemoveSucceeds = false };
        var api = new FakeDeviceTrustApiClient();
        var service = CreateService(
            session,
            tokenStore,
            deviceKeyStore,
            api);
        service.SetCurrentSessionCapability(session.CaptureLease(), supported: true);

        await Assert.ThrowsAsync<DeviceTrustException>(
            () => service.TrustCurrentDeviceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["server-device-id"], api.LastUntrustedDeviceIds);
        Assert.Equal(1, deviceKeyStore.RemoveCalls);
        Assert.True(deviceKeyStore.Contains(CurrentBinding()));
        Assert.False(service.IsCurrentDeviceTrusted);
        Assert.True(service.CanTrustCurrentDevice);
        Assert.True(tokenStore.TryLoad(out var failedPersisted));
        Assert.Equal(PersistedSessionUnlockMethod.MasterPassword, failedPersisted.UnlockMethod);

        tokenStore.ThrowOnSave = false;
        await service.TrustCurrentDeviceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, api.UpdateCalls);
        Assert.Equal(2, deviceKeyStore.SaveCalls);
        Assert.True(service.IsCurrentDeviceTrusted);
        Assert.False(service.CanTrustCurrentDevice);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal(PersistedSessionUnlockMethod.TrustedDeviceSso, persisted.UnlockMethod);
    }

    [Fact]
    public async Task TrustCurrentDevice_ConcurrentCallsUploadOnlyOneKeySet()
    {
        var session = CreateUnlockedSession(out _);
        var store = CreatePersistedStore(session);
        var api = new FakeDeviceTrustApiClient();
        var service = CreateService(
            session,
            store,
            new MemoryDeviceKeyStore(),
            api);
        service.SetCurrentSessionCapability(session.CaptureLease(), supported: true);

        var results = await Task.WhenAll(
            CaptureAsync(() => service.TrustCurrentDeviceAsync(
                TestContext.Current.CancellationToken)),
            CaptureAsync(() => service.TrustCurrentDeviceAsync(
                TestContext.Current.CancellationToken)));

        Assert.Equal(1, api.UpdateCalls);
        Assert.Single(results, result => result is null);
        Assert.Single(results, result => result is DeviceTrustException);
    }

    [Fact]
    public void SetCurrentSessionCapability_DoesNotCrossNewGeneration()
    {
        var session = CreateUnlockedSession(out _);
        var persistedStore = CreatePersistedStore(session);
        var service = CreateService(
            session,
            persistedStore,
            new MemoryDeviceKeyStore(),
            new FakeDeviceTrustApiClient());
        var originalLease = session.CaptureLease();
        service.SetCurrentSessionCapability(originalLease, supported: true);

        Assert.True(service.CanTrustCurrentDevice);

        var newLease = session.BeginAccountSession(
            AccountId,
            "me@example.com",
            Endpoints);
        session.SetTokens("new-access-token", "new-refresh-token");
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        Assert.True(persistedStore.TryLoad(out var persisted));
        persistedStore.Save(persisted with { Generation = newLease.Generation });

        Assert.NotEqual(originalLease.Generation, newLease.Generation);
        Assert.False(service.CanTrustCurrentDevice);

        service.SetCurrentSessionCapability(newLease, supported: true);

        Assert.True(service.CanTrustCurrentDevice);
    }

    [Fact]
    public async Task TrustCurrentDevice_LeaseExpiresAfterUpload_UsesFrozenCompensationBinding()
    {
        var session = CreateUnlockedSession(out _);
        var lease = session.CaptureLease();
        var store = CreatePersistedStore(session);
        var api = new FakeDeviceTrustApiClient
        {
            AfterUpdate = () => session.TryLockIfCurrent(lease),
        };
        var compensation = new FakeCompensationClient();
        var service = CreateService(
            session,
            store,
            new MemoryDeviceKeyStore(),
            api,
            compensation);
        service.SetCurrentSessionCapability(lease, supported: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.TrustCurrentDeviceAsync(TestContext.Current.CancellationToken));

        Assert.Equal("access-token", compensation.Binding!.Token);
        Assert.Equal("server-device-id", compensation.DeviceId);
    }

    private static DeviceTrustService CreateService(
        VaultSession session,
        ITokenStore tokenStore,
        IDeviceKeyStore deviceKeyStore,
        IDeviceTrustApiClient api,
        IDeviceTrustCompensationClient? compensation = null) =>
        new(
            FakeVaultApiClientProvider.ForAuthenticated(api),
            new TrustedDeviceCrypto(new CryptoService()),
            deviceKeyStore,
            session,
            tokenStore,
            compensation);

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static VaultSession CreateUnlockedSession(out byte[] userKey)
    {
        userKey = Enumerable.Range(1, 64).Select(value => (byte)value).ToArray();
        var session = new VaultSession();
        session.BeginAccountSession(AccountId, "me@example.com", Endpoints);
        session.SetTokens("access-token", "refresh-token");
        session.SetUnlockedKey(new SymmetricCryptoKey(userKey.ToArray()));
        return session;
    }

    private static MemoryTokenStore CreatePersistedStore(VaultSession session)
    {
        var lease = session.CaptureLease();
        var store = new MemoryTokenStore();
        store.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            AccountId,
            Endpoints,
            lease.Generation,
            "me@example.com",
            DeviceIdentifier,
            "refresh-token",
            "2.protected-user-key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null));
        return store;
    }

    private static DeviceKeyBinding CurrentBinding() =>
        new(AccountId, Endpoints, DeviceIdentifier);

    private sealed class MemoryDeviceKeyStore : IDeviceKeyStore
    {
        private readonly Dictionary<DeviceKeyBinding, byte[]> _keys = [];
        public bool ThrowOnSave { get; init; }
        public bool RemoveSucceeds { get; set; } = true;
        public int TryLoadCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public int RemoveCalls { get; private set; }

        public bool TryLoad(DeviceKeyBinding binding, out byte[] deviceKey)
        {
            TryLoadCalls++;
            if (_keys.TryGetValue(binding, out var stored))
            {
                deviceKey = stored.ToArray();
                return true;
            }
            deviceKey = [];
            return false;
        }

        public void Save(DeviceKeyBinding binding, ReadOnlySpan<byte> deviceKey)
        {
            SaveCalls++;
            if (ThrowOnSave)
                throw new IOException("simulated storage failure");
            if (_keys.TryGetValue(binding, out var previous))
                CryptographicOperations.ZeroMemory(previous);
            _keys[binding] = deviceKey.ToArray();
        }

        public bool Remove(DeviceKeyBinding binding)
        {
            RemoveCalls++;
            if (!RemoveSucceeds || !_keys.Remove(binding, out var removed))
                return false;
            CryptographicOperations.ZeroMemory(removed);
            return true;
        }

        public bool Contains(DeviceKeyBinding binding) => _keys.ContainsKey(binding);
    }

    private sealed class SwitchableTokenStore(ITokenStore inner) : ITokenStore
    {
        public bool ThrowOnSave { get; set; }

        public bool TryLoad(out PersistedSession session) => inner.TryLoad(out session);

        public void Save(PersistedSession session)
        {
            if (ThrowOnSave)
                throw new IOException("simulated token storage failure");
            inner.Save(session);
        }

        public void Clear() => inner.Clear();
    }

    private sealed class FakeDeviceTrustApiClient : IDeviceTrustApiClient
    {
        public int UpdateCalls { get; private set; }
        public Action? AfterUpdate { get; init; }
        public TrustedDeviceKeysRequest? LastKeys { get; private set; }
        public string[] LastUntrustedDeviceIds { get; private set; } = [];

        public Task<DeviceDto> UpdateTrustedDeviceKeysAsync(
            string deviceIdentifier,
            TrustedDeviceKeysRequest request,
            CancellationToken ct = default)
        {
            UpdateCalls++;
            LastKeys = request;
            var response = new DeviceDto(
                "server-device-id",
                "Desktop",
                6,
                deviceIdentifier,
                DateTimeOffset.UtcNow,
                true);
            AfterUpdate?.Invoke();
            return Task.FromResult(response);
        }

        public Task UntrustDevicesAsync(
            UntrustDevicesRequest request,
            CancellationToken ct = default)
        {
            LastUntrustedDeviceIds = request.Devices;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCompensationClient : IDeviceTrustCompensationClient
    {
        public Core.Session.AccessTokenBinding? Binding { get; private set; }
        public string? DeviceId { get; private set; }

        public Task UntrustAsync(
            Core.Session.AccessTokenBinding binding,
            string deviceId,
            CancellationToken ct = default)
        {
            Binding = binding;
            DeviceId = deviceId;
            return Task.CompletedTask;
        }
    }
}
