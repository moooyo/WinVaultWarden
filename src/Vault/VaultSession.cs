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
    public int SkippedCipherCount { get; private set; }

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

    // 仅当仍持 UserKey(未被并发 Lock/Clear)时把状态迁移到 Unlocked(S8)。
    // 避免 bootstrap 结尾无条件置 Unlocked 覆盖并发锁定,导致 State=Unlocked 但 UserKey=null 的矛盾态
    // (UI 以为已解锁却是空库、写操作抛"Vault is locked")。检查与赋值在同一 _gate 下,与 Lock() 原子互斥。
    public void MarkUnlockedIfKeyPresent()
    {
        lock (_gate)
        {
            if (UserKey is not null)
                State = VaultState.Unlocked;
        }
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
            // 并发锁定/登出防护(S6):若在途同步期间已被 Lock()/Clear()(UserKey 已在同一 _gate 下清零置空),
            // 不得把解密后的明文条目回灌进已锁定会话——否则会出现 State=Locked 却仍能读到整库明文的矛盾态。
            if (UserKey is null)
                return;
            Account = vault.Account;
            _folders = vault.Folders;
            _ciphers = vault.Ciphers;
            SkippedCipherCount = vault.SkippedCipherCount;
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
        // Dispose 清零 UserKey 的 FullKey/EncKey/MacKey 全部支撑数组(等价于原逐一 ZeroMemory,更简洁)。
        UserKey?.Dispose();

        // 【已知残余风险 — 托管明文残留】锁定/登出这里只能清零 UserKey 的字节数组(可变 byte[],
        // 可就地 ZeroMemory)。而解密后的 Cipher 字段明文(用户名/密码/备注等)是不可变的
        // System.String,CLR 不提供就地清零手段:即便把引用置空(下方 _ciphers = Empty),
        // 明文字符串仍作为托管堆对象存在,直到 GC 回收并压缩堆之前一直残留在内存中。
        // 因此锁定后、下一次 GC 前的内存转储仍可能包含明文。这是托管运行时的固有限制,
        // Bitwarden 官方桌面客户端(基于同类托管/JS 运行时)也存在同样约束,属已知残余风险。
        // (如需进一步缓解可在锁定后 GC.Collect(),但不能根除,本批不引入。)
        AccessToken = null;
        RefreshToken = null;
        UserKey = null;
        _ciphers = Array.Empty<Cipher>();
        _folders = Array.Empty<Folder>();
        _devices = Array.Empty<DeviceInfo>();
        EncryptedPrivateKey = null;
        SkippedCipherCount = 0;
    }
}
