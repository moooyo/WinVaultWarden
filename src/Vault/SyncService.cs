using Api;
using Core.Models;
using Core.Services;

namespace Vault;

public sealed class SyncService : ISyncService
{
    private readonly IReadonlyApiClient _api;
    private readonly VaultDecryptor _decryptor;
    private readonly VaultSession _session;

    public SyncService(IReadonlyApiClient api, VaultDecryptor decryptor, VaultSession session)
    {
        _api = api;
        _decryptor = decryptor;
        _session = session;
    }

    public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default) =>
        SyncCoreAsync(_session.Account.ServerUrl, ct);

    // 拉取 + 解密 + 存快照的核心逻辑,按 serverUrl 参数化(唯一实现处)。
    // serverUrl 显式传入:首次 bootstrap 时 _session.Account 尚未就绪(ServerUrl 为空),
    // 必须由调用方(VaultBootstrapper)提供登录/解锁时已知的 serverUrl,以正确构建 Account.ServerUrl。
    public async Task<IReadOnlyList<Cipher>> SyncCoreAsync(string serverUrl, CancellationToken ct = default)
    {
        var userKey = _session.UserKey ?? throw new InvalidOperationException("Vault is locked.");
        var sync = await _api.GetSyncAsync(ct);
        var vault = _decryptor.Decrypt(sync, userKey, serverUrl);
        _session.SetSnapshot(vault);
        _session.SetEncryptedPrivateKey(sync.Profile?.PrivateKey);
        return vault.Ciphers;
    }
}
