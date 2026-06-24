using Core.Models;
using Core.Session;
using Crypto;

namespace Vault;

public sealed class VaultSession : IVaultSnapshot
{
    private readonly object _gate = new();
    private IReadOnlyList<Cipher> _ciphers = Array.Empty<Cipher>();
    private IReadOnlyList<Folder> _folders = Array.Empty<Folder>();
    private IReadOnlyList<DeviceInfo> _devices = Array.Empty<DeviceInfo>();

    public VaultState State { get; private set; } = VaultState.LoggedOut;
    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public SymmetricCryptoKey? UserKey { get; private set; }
    public AccountInfo Account { get; private set; } = AccountInfo.Empty;

    public IReadOnlyList<Cipher> Ciphers
    {
        get { lock (_gate) return _ciphers; }
    }

    public IReadOnlyList<Folder> Folders
    {
        get { lock (_gate) return _folders; }
    }

    public IReadOnlyList<DeviceInfo> Devices
    {
        get { lock (_gate) return _devices; }
    }

    public void SetState(VaultState state)
    {
        lock (_gate)
            State = state;
    }

    public void SetTokens(string accessToken, string refreshToken)
    {
        lock (_gate)
        {
            AccessToken = accessToken;
            RefreshToken = refreshToken;
        }
    }

    public void SetUnlockedKey(SymmetricCryptoKey userKey)
    {
        lock (_gate)
        {
            UserKey = userKey;
            State = VaultState.Unlocked;
        }
    }

    public void SetSnapshot(DecryptedVault vault)
    {
        lock (_gate)
        {
            Account = vault.Account;
            _folders = vault.Folders;
            _ciphers = vault.Ciphers;
        }
    }

    public void SetDevices(IReadOnlyList<DeviceInfo> devices)
    {
        lock (_gate)
            _devices = devices;
    }

    public void SetAccount(AccountInfo account)
    {
        lock (_gate)
            Account = account;
    }

    public void Lock()
    {
        lock (_gate)
        {
            UserKey = null;
            State = VaultState.Locked;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            AccessToken = null;
            RefreshToken = null;
            UserKey = null;
            Account = AccountInfo.Empty;
            _ciphers = Array.Empty<Cipher>();
            _folders = Array.Empty<Folder>();
            _devices = Array.Empty<DeviceInfo>();
            State = VaultState.LoggedOut;
        }
    }
}
