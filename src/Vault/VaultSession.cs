using System.Security.Cryptography;
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
    public string? EncryptedPrivateKey { get; private set; }
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

    public void SetEncryptedPrivateKey(string? encryptedPrivateKey)
    {
        lock (_gate)
            EncryptedPrivateKey = encryptedPrivateKey;
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

    public void UpsertCipher(Cipher c)
    {
        lock (_gate)
        {
            var list = new List<Cipher>(_ciphers.Count + 1);
            var replaced = false;
            foreach (var existing in _ciphers)
            {
                if (existing.Id == c.Id)
                {
                    list.Add(c);
                    replaced = true;
                }
                else
                {
                    list.Add(existing);
                }
            }
            if (!replaced)
                list.Add(c);
            _ciphers = list;
        }
    }

    public void RemoveCipher(string id)
    {
        lock (_gate)
        {
            var list = new List<Cipher>(_ciphers.Count);
            foreach (var existing in _ciphers)
            {
                if (existing.Id != id)
                    list.Add(existing);
            }
            _ciphers = list;
        }
    }

    public void UpsertFolder(Folder f)
    {
        lock (_gate)
        {
            var list = new List<Folder>(_folders.Count + 1);
            var replaced = false;
            foreach (var existing in _folders)
            {
                if (existing.Id == f.Id)
                {
                    list.Add(f);
                    replaced = true;
                }
                else
                {
                    list.Add(existing);
                }
            }
            if (!replaced)
                list.Add(f);
            _folders = list;
        }
    }

    public void RemoveFolder(string id)
    {
        lock (_gate)
        {
            var list = new List<Folder>(_folders.Count);
            foreach (var existing in _folders)
            {
                if (existing.Id != id)
                    list.Add(existing);
            }
            _folders = list;
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
            ClearSensitiveState();
            State = VaultState.Locked;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ClearSensitiveState();
            Account = AccountInfo.Empty;
            State = VaultState.LoggedOut;
        }
    }

    private void ClearSensitiveState()
    {
        if (UserKey is not null)
        {
            CryptographicOperations.ZeroMemory(UserKey.FullKey);
            CryptographicOperations.ZeroMemory(UserKey.EncKey);
            if (UserKey.MacKey is not null)
                CryptographicOperations.ZeroMemory(UserKey.MacKey);
        }

        AccessToken = null;
        RefreshToken = null;
        UserKey = null;
        _ciphers = Array.Empty<Cipher>();
        _folders = Array.Empty<Folder>();
        _devices = Array.Empty<DeviceInfo>();
        EncryptedPrivateKey = null;
    }
}
