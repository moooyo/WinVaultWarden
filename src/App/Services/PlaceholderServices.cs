using Core.Models;
using Core.Services;
using Core.Session;

namespace App.Services;

public sealed class AuthService : IAuthService
{
    public Task<AuthResult> LoginAsync(string serverUrl, string email, string masterPassword, CancellationToken ct = default)
        => Task.FromResult<AuthResult>(new AuthResult.Failure("真实认证服务尚未接入。"));

    public Task<AuthResult> SubmitTwoFactorAsync(string code, CancellationToken ct = default, bool rememberDevice = true)
        => Task.FromResult<AuthResult>(new AuthResult.Failure("两步验证服务尚未接入。"));

    public Task<AuthResult> UnlockAsync(string masterPassword, CancellationToken ct = default)
        => Task.FromResult<AuthResult>(new AuthResult.Failure("解锁服务尚未接入。"));

    public Task LockAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class SyncService : ISyncService
{
    public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Cipher>>(Array.Empty<Cipher>());
}

public sealed class VaultService : IVaultService
{
    public IVaultSnapshot Snapshot { get; } = EmptyVaultSnapshot.Instance;

    public IReadOnlyList<Cipher> GetCiphers() => Snapshot.Ciphers;

    public IReadOnlyList<Folder> GetFolders() => Snapshot.Folders;

    public IReadOnlyList<DeviceInfo> GetDevices() => Snapshot.Devices;
}

internal sealed class EmptyVaultSnapshot : IVaultSnapshot
{
    public static EmptyVaultSnapshot Instance { get; } = new();

    public VaultState State => VaultState.LoggedOut;
    public IReadOnlyList<Cipher> Ciphers { get; } = Array.Empty<Cipher>();
    public IReadOnlyList<Folder> Folders { get; } = Array.Empty<Folder>();
    public IReadOnlyList<DeviceInfo> Devices { get; } = Array.Empty<DeviceInfo>();
    public AccountInfo Account => AccountInfo.Empty;
}
