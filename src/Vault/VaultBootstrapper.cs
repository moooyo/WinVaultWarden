using Api;
using Core.Models;
using Core.Session;

namespace Vault;

public sealed class VaultBootstrapper
{
    private readonly IReadonlyApiClient _api;
    private readonly VaultDecryptor _decryptor;
    private readonly VaultSession _session;

    public VaultBootstrapper(IReadonlyApiClient api, VaultDecryptor decryptor, VaultSession session)
    {
        _api = api;
        _decryptor = decryptor;
        _session = session;
    }

    public async Task BootstrapAsync(string serverUrl, CancellationToken ct = default)
    {
        var userKey = _session.UserKey ?? throw new InvalidOperationException("Vault is locked.");
        _session.SetState(VaultState.Syncing);

        var sync = await _api.GetSyncAsync(ct);
        var vault = _decryptor.Decrypt(sync, userKey, serverUrl);
        _session.SetSnapshot(vault);
        _session.SetEncryptedPrivateKey(sync.Profile?.PrivateKey);

        var devices = await _api.GetDevicesAsync(ct);
        _session.SetDevices(devices.Data.Select(MapDevice).ToArray());
        _session.SetState(VaultState.Unlocked);
    }

    private static DeviceInfo MapDevice(Api.Dtos.DeviceDto device) => new(
        device.Id,
        device.Name,
        device.Type,
        device.Identifier,
        device.CreationDate,
        device.IsTrusted);
}
