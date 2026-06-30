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

    public async Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default)
    {
        var userKey = _session.UserKey ?? throw new InvalidOperationException("Vault is locked.");
        var sync = await _api.GetSyncAsync(ct);
        var vault = _decryptor.Decrypt(sync, userKey, _session.Account.ServerUrl);
        _session.SetSnapshot(vault);
        _session.SetEncryptedPrivateKey(sync.Profile?.PrivateKey);
        return vault.Ciphers;
    }
}
