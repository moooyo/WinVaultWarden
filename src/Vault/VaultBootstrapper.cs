using Api;
using Core.Models;
using Core.Session;

namespace Vault;

public sealed class VaultBootstrapper
{
    private readonly SyncService _sync;
    private readonly IReadonlyApiClient _api;
    private readonly VaultSession _session;

    public VaultBootstrapper(SyncService sync, IReadonlyApiClient api, VaultSession session)
    {
        _sync = sync;
        _api = api;
        _session = session;
    }

    public async Task BootstrapAsync(string serverUrl, CancellationToken ct = default)
    {
        // 解锁后的初始数据装载 = 整库同步(委托 SyncService.SyncCoreAsync,serverUrl 显式传入)
        // + 拉取设备 + 状态机迁移。同步/解密逻辑不在此重复。
        _ = _session.UserKey ?? throw new InvalidOperationException("Vault is locked.");
        _session.SetState(VaultState.Syncing);

        await _sync.SyncCoreAsync(serverUrl, ct);

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
