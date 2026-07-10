using Api;
using Core.Models;
using Core.Session;

namespace Vault;

public sealed class VaultBootstrapper
{
    private readonly SyncService _sync;
    private readonly IVaultApiClientProvider _clients;
    private readonly VaultSession _session;

    public VaultBootstrapper(SyncService sync, IVaultApiClientProvider clients, VaultSession session)
    {
        _sync = sync;
        _clients = clients;
        _session = session;
    }

    public async Task BootstrapAsync(
        VaultSessionLease lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        // 状态迁入 Syncing 也须绑定当前租约:租约已失效则整体放弃(抛取消)。
        if (!_session.TryCommit(lease, () => _session.SetState(VaultState.Syncing)))
            throw new OperationCanceledException(lease.CancellationToken);

        // 解锁后的初始数据装载 = 整库同步(委托 SyncService,lease 传入,同步/解密逻辑不重复)
        // + 拉取设备 + 状态机迁移。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, lease.CancellationToken);
        await _sync.SyncAsync(lease, linked.Token).ConfigureAwait(false);

        var api = _clients.GetAuthenticated<IReadonlyApiClient>(lease);
        var devices = await api.GetDevicesAsync(linked.Token).ConfigureAwait(false);

        // devices 与 Unlocked 状态在同一 TryCommit 门禁提交:若 devices 在途期间被并发 Lock/Logout/
        // Switch(即使 fake 忽略取消仍返回),既不回灌设备也不强行迁回 Unlocked——抛取消。
        if (!_session.TryCommit(lease, () =>
        {
            _session.SetDevices(devices.Data.Select(MapDevice).ToArray());
            _session.SetState(VaultState.Unlocked);
        }))
            throw new OperationCanceledException(lease.CancellationToken);
    }

    private static DeviceInfo MapDevice(Api.Dtos.DeviceDto device) => new(
        device.Id,
        device.Name,
        device.Type,
        device.Identifier,
        device.CreationDate,
        device.IsTrusted);
}
