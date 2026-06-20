using Core.Models;
using Core.Services;

namespace App.Services;

public sealed class AuthService : IAuthService
{
    public Task LoginAsync(string serverUrl, string email, string masterPassword, CancellationToken ct = default)
        => throw new NotImplementedException("TODO: prelogin → 派生 → connect/token");
}

public sealed class SyncService : ISyncService
{
    public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Cipher>>(Array.Empty<Cipher>());
}

public sealed class VaultService : IVaultService
{
    public IReadOnlyList<Cipher> GetCiphers() => Array.Empty<Cipher>();
}
