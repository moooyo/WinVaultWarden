using Core.Models;
using Core.Session;
using Crypto;
using Crypto.V2;

namespace Vault;

public sealed class VaultSession : IVaultSnapshot
{
    private readonly object _gate = new();
    private IReadOnlyList<Cipher> _ciphers = Array.Empty<Cipher>();
    private IReadOnlyList<Folder> _folders = Array.Empty<Folder>();
    private IReadOnlyList<DeviceInfo> _devices = Array.Empty<DeviceInfo>();
    private IReadOnlyList<Send> _sends = Array.Empty<Send>();
    private IReadOnlyList<VaultOrganization> _organizations = Array.Empty<VaultOrganization>();
    private IReadOnlyList<VaultCollection> _collections = Array.Empty<VaultCollection>();
    private IReadOnlyList<VaultOrganizationPolicy> _policies = Array.Empty<VaultOrganizationPolicy>();
    private IReadOnlyList<VaultOrganizationPolicyContext> _policyOrganizations =
        Array.Empty<VaultOrganizationPolicyContext>();
    private VaultDomainSettings? _domains;
    private VaultCapabilities _capabilities = VaultCapabilities.None;
    private readonly Dictionary<(string AccountId, ServerEndpoints Endpoints), string>
        _securityStamps = [];
    private readonly LinkedList<(string AccountId, ServerEndpoints Endpoints)>
        _securityStampRecency = [];

    // Generation/stamp/CTS 是本会话身份轮换的唯一真源(见类尾 lease 相关方法)。
    private long _generation;
    private string? _accountId;
    private ServerEndpoints? _endpoints;
    private Guid _stamp;
    private CancellationTokenSource? _leaseCts;
    private long _authenticationGeneration;
    private Guid _authenticationStamp;
    private CancellationTokenSource? _authenticationCts;

    public VaultState State { get; private set; } = VaultState.LoggedOut;
    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    private VaultUserKey? _userKey;
    public SymmetricCryptoKey? UserKey
    {
        get { lock (_gate) return _userKey?.Legacy; }
    }
    public bool UsesV2UserKey
    {
        get { lock (_gate) return _userKey?.IsV2 == true; }
    }
    public int SecurityVersion
    {
        get { lock (_gate) return _userKey?.SecurityVersion ?? 0; }
    }
    public string? EncryptedPrivateKey { get; private set; }
    public AccountInfo Account { get; private set; } = AccountInfo.Empty;
    public int SkippedCipherCount { get; private set; }
    public int SkippedFolderCount { get; private set; }
    public int SkippedSendCount { get; private set; }
    public int UnsupportedOrganizationCipherCount { get; private set; }
    public int UnsupportedProviderOrganizationCount { get; private set; }
    public bool DataStale { get; private set; }
    public VaultCapabilities Capabilities
    {
        get { lock (_gate) return _capabilities; }
    }

    public long Generation
    {
        get { lock (_gate) return _generation; }
    }

    // 演示/离线会话标记(DEBUG「使用演示保险库」入口置位)。为 true 时,网络回读路径改从会话内存中
    // 的离线数据取值,不发起任何 HTTP 请求——演示会话没有配置服务端地址,任何相对 URI 请求都会抛
    // 「An invalid request URI was provided…」。Lock()/Clear() 经 ClearSensitiveState 复位。
    public bool IsDemo { get; private set; }

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

    public IReadOnlyList<VaultOrganization> Organizations
    {
        get { lock (_gate) return _organizations; }
    }

    public IReadOnlyList<VaultCollection> Collections
    {
        get { lock (_gate) return _collections; }
    }

    public IReadOnlyList<VaultOrganizationPolicy> Policies
    {
        get { lock (_gate) return _policies; }
    }

    public IReadOnlyList<VaultOrganizationPolicyContext> PolicyOrganizations
    {
        get { lock (_gate) return _policyOrganizations; }
    }

    public VaultDomainSettings? Domains
    {
        get { lock (_gate) return _domains; }
    }

    // 演示/离线会话的 Send 列表(明文领域模型,仅存内存,不落盘/发日志——与 ShareUrl/Key 同约束)。
    // 在线会话不使用此列表:Send 始终经 SendService 实时从 /api/sends 拉取并解密。
    public IReadOnlyList<Send> Sends
    {
        get { lock (_gate) return _sends; }
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
            if (_userKey is not null)
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

    // B2:离线解锁成功后把持久化 refresh token 种回内存(AccessToken 仍为 null)。
    // 使后续手动/自动 Sync 撞 401 时 TokenRefreshService 能凭此触发真实刷新、透明转在线。
    public void SetRefreshTokenOnly(string refreshToken)
    {
        lock (_gate)
            RefreshToken = refreshToken;
    }

    public void SetUnlockedKey(SymmetricCryptoKey userKey)
    {
        ArgumentNullException.ThrowIfNull(userKey);
        if (userKey.IsDisposed)
            throw new ObjectDisposedException(nameof(userKey));
        lock (_gate)
        {
            if (ReferenceEquals(_userKey?.Legacy, userKey))
            {
                State = VaultState.Unlocked;
                return;
            }
            _userKey?.Dispose();
            _userKey = VaultUserKey.TakeOwnership(userKey);
            State = VaultState.Unlocked;
        }
    }

    public void SetUnlockedKey(V2UserKey userKey)
    {
        ArgumentNullException.ThrowIfNull(userKey);
        _ = userKey.KeyIdHex;
        lock (_gate)
        {
            if (ReferenceEquals(_userKey?.V2, userKey))
            {
                State = VaultState.Unlocked;
                return;
            }
            _userKey?.Dispose();
            _userKey = VaultUserKey.TakeOwnership(userKey);
            State = VaultState.Unlocked;
        }
    }

    public void SetUnlockedKey(VaultUserKey userKey)
    {
        ArgumentNullException.ThrowIfNull(userKey);
        _ = userKey.SecurityVersion;
        lock (_gate)
        {
            if (ReferenceEquals(_userKey, userKey))
            {
                State = VaultState.Unlocked;
                return;
            }
            if (_userKey?.SharesBackingKeyWith(userKey) == true)
            {
                userKey.RelinquishBackingKeyOwnership();
                State = VaultState.Unlocked;
                return;
            }

            _userKey?.Dispose();
            _userKey = userKey;
            State = VaultState.Unlocked;
        }
    }

    public void SetEncryptedPrivateKey(string? encryptedPrivateKey)
    {
        lock (_gate)
            EncryptedPrivateKey = encryptedPrivateKey;
    }

    /// <summary>写入快照。返回是否实际应用(false 表示会话已被并发 Lock/Clear,快照被丢弃)。</summary>
    public bool SetSnapshot(
        DecryptedVault vault,
        bool dataStale = false,
        VaultCapabilities? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(vault);
        lock (_gate)
        {
            // 并发锁定/登出防护(S6):若在途同步期间已被 Lock()/Clear()(UserKey 已在同一 _gate 下清零置空),
            // 不得把解密后的明文条目回灌进已锁定会话——否则会出现 State=Locked 却仍能读到整库明文的矛盾态。
            if (_userKey is null)
                return false;
            Account = vault.Account;
            _folders = vault.Folders;
            _ciphers = vault.Ciphers;
            _sends = vault.Sends;
            _organizations = vault.Organizations;
            _collections = vault.Collections;
            _policies = vault.Policies;
            _policyOrganizations = vault.PolicyOrganizations
                ?? vault.Organizations.Select(organization =>
                    new VaultOrganizationPolicyContext(
                        organization.Id,
                        organization.Status,
                        organization.Type,
                        organization.Enabled,
                        UsePolicies: true,
                        IsProviderUser: organization.IsProviderUser
                            || organization.IsProviderOrganization)).ToArray();
            _domains = vault.Domains;
            SkippedCipherCount = vault.SkippedCipherCount;
            SkippedFolderCount = vault.SkippedFolderCount;
            SkippedSendCount = vault.SkippedSendCount;
            UnsupportedOrganizationCipherCount = vault.UnsupportedOrganizationCipherCount;
            UnsupportedProviderOrganizationCount = vault.UnsupportedProviderOrganizationCount;
            _capabilities = capabilities ?? VaultCapabilities.None;
            DataStale = dataStale;
            return true;
        }
    }

    public bool TrySetCapabilities(VaultSessionLease lease, VaultCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(capabilities);
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease))
                return false;
            _capabilities = capabilities;
            return true;
        }
    }

    public VaultCapabilities GetCapabilities(VaultSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease))
                throw new OperationCanceledException(lease.CancellationToken);
            return _capabilities;
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

    // 删除文件夹时把引用它的条目本地迁到"无文件夹"(FolderId=null)。服务端删文件夹只推单条
    // SyncFolderDelete、不逐条推 cipher,官方客户端需本地做此迁移,否则这些条目文件夹名渲染为空/
    // 分组视图出现重复"无文件夹"头,直到下次整库同步才自愈(R8)。copy-on-write,持锁读一致。
    public void ClearFolderReference(string folderId)
    {
        lock (_gate)
        {
            if (_ciphers.All(c => c.FolderId != folderId))
                return;
            _ciphers = _ciphers
                .Select(c => c.FolderId == folderId ? c.WithFolderId(null) : c)
                .ToArray();
        }
    }

    public void SetDevices(IReadOnlyList<DeviceInfo> devices)
    {
        lock (_gate)
            _devices = devices;
    }

    // ── 演示/离线 Send 存储(仅演示会话使用)──────────────────────────────────────
    // 标记本会话为演示/离线会话。之后 Send 的读写走会话内存,不触网(见 IsDemo 注释)。
    public void MarkDemo()
    {
        lock (_gate)
            IsDemo = true;
    }

    public void SetSends(IReadOnlyList<Send> sends)
    {
        lock (_gate)
            _sends = sends;
    }

    public bool TrySetSends(VaultSessionLease lease, IReadOnlyList<Send> sends)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sends);
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease) || _userKey is null)
                return false;
            _sends = sends;
            return true;
        }
    }

    public bool TryGetSendSnapshot(
        VaultSessionLease lease,
        out IReadOnlyList<Send> sends,
        out bool dataStale)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease) || _userKey is null)
            {
                sends = Array.Empty<Send>();
                dataStale = false;
                return false;
            }

            sends = _sends;
            dataStale = DataStale;
            return true;
        }
    }

    public bool IsDataStale(VaultSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease) || _userKey is null)
                throw new OperationCanceledException(lease.CancellationToken);
            return DataStale;
        }
    }

    // copy-on-write 插入/替换(按 Id),与 UpsertCipher/UpsertFolder 一致的健壮策略。
    public void UpsertSend(Send send)
    {
        lock (_gate)
        {
            var list = new List<Send>(_sends.Count + 1);
            var replaced = false;
            foreach (var existing in _sends)
            {
                if (existing.Id == send.Id)
                {
                    list.Add(send);
                    replaced = true;
                }
                else
                {
                    list.Add(existing);
                }
            }
            if (!replaced)
                list.Add(send);
            _sends = list;
        }
    }

    public void RemoveSend(string id)
    {
        lock (_gate)
        {
            var list = new List<Send>(_sends.Count);
            foreach (var existing in _sends)
            {
                if (existing.Id != id)
                    list.Add(existing);
            }
            _sends = list;
        }
    }

    public void SetAccount(AccountInfo account)
    {
        lock (_gate)
            Account = account;
    }

    public AuthenticationAttemptLease BeginAuthenticationAttempt()
    {
        lock (_gate)
        {
            RotateAuthenticationAttemptNoLock(createCurrent: true);
            return CurrentAuthenticationAttemptNoLock();
        }
    }

    public bool IsCurrent(AuthenticationAttemptLease attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate) return IsCurrentNoLock(attempt);
    }

    public void AbandonAuthenticationAttempt(AuthenticationAttemptLease attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate)
        {
            if (IsCurrentNoLock(attempt))
                RotateAuthenticationAttemptNoLock(createCurrent: false);
        }
    }

    public bool TryBeginAccountSession(
        AuthenticationAttemptLease attempt,
        string accountId,
        string email,
        ServerOrigin origin,
        out VaultSessionLease? lease) =>
        TryBeginAccountSession(
            attempt,
            accountId,
            email,
            ServerEndpoints.ForSelfHosted(origin.Value),
            out lease);

    public bool TryBeginAccountSession(
        AuthenticationAttemptLease attempt,
        string accountId,
        string email,
        ServerEndpoints endpoints,
        out VaultSessionLease? lease)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        lock (_gate)
        {
            if (!IsCurrentNoLock(attempt))
            {
                lease = null;
                return false;
            }

            RotateAuthenticationAttemptNoLock(createCurrent: false);
            BeginAccountSessionNoLock(accountId, email, endpoints);
            lease = CurrentLeaseNoLock();
            return true;
        }
    }

    public bool TryRestorePersistedSession(
        AuthenticationAttemptLease attempt,
        PersistedSession persisted,
        out VaultSessionLease? lease)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(persisted);
        if (!persisted.HasValidBinding)
            throw new InvalidDataException("Persisted session binding is invalid.");

        lock (_gate)
        {
            if (!IsCurrentNoLock(attempt))
            {
                lease = null;
                return false;
            }

            RotateAuthenticationAttemptNoLock(createCurrent: false);
            RestorePersistedSessionNoLock(persisted);
            lease = CurrentLeaseNoLock();
            return true;
        }
    }

    internal bool TryClearMatchingAccountForAuthentication(
        AuthenticationAttemptLease attempt,
        string accountId,
        ServerEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);
        lock (_gate)
        {
            if (!IsCurrentNoLock(attempt))
                return false;
            if (_accountId != accountId || _endpoints != endpoints)
                return true;

            try
            {
                RotateLeaseNoLock(null, null, persistedGeneration: null);
            }
            finally
            {
                RemoveSecurityStampNoLock((accountId, endpoints));
                ClearSensitiveState();
                Account = AccountInfo.Empty;
                State = VaultState.LoggedOut;
            }

            return true;
        }
    }

    /// <summary>
    /// 以给定账户身份开启一个新会话:轮换 generation、取消旧租约、清空敏感状态,
    /// 并返回绑定新 generation 的租约供调用方随后 TryCommit 写入解密后的数据。
    /// </summary>
    public VaultSessionLease BeginAccountSession(string accountId, string email, ServerOrigin origin)
        => BeginAccountSession(
            accountId,
            email,
            ServerEndpoints.ForSelfHosted(origin.Value));

    public VaultSessionLease BeginAccountSession(
        string accountId,
        string email,
        ServerEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        lock (_gate)
        {
            RotateAuthenticationAttemptNoLock(createCurrent: false);
            BeginAccountSessionNoLock(accountId, email, endpoints);
            return CurrentLeaseNoLock();
        }
    }

    /// <summary>
    /// 从磁盘恢复的持久化会话重建租约。若当前内存会话已经就是同一 account+origin+generation
    /// 且租约尚未取消,直接复用现有租约(幂等),否则轮换 generation 并清空敏感状态后进入 Locked。
    /// </summary>
    public VaultSessionLease RestorePersistedSession(PersistedSession persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        if (!persisted.HasValidBinding)
            throw new InvalidDataException("Persisted session binding is invalid.");

        lock (_gate)
        {
            RotateAuthenticationAttemptNoLock(createCurrent: false);
            RestorePersistedSessionNoLock(persisted);
            return CurrentLeaseNoLock();
        }
    }

    /// <summary>捕获当前会话的租约快照,供后续 IsCurrent/TryCommit 校验之用。无活跃会话则抛出。</summary>
    public VaultSessionLease CaptureLease()
    {
        lock (_gate)
        {
            if (_accountId is null || _endpoints is null || _leaseCts is null)
                throw new InvalidOperationException("No account session is active.");
            return CurrentLeaseNoLock();
        }
    }

    /// <summary>租约是否仍对应当前 generation/账户/来源(未被 Lock/Logout/Switch 轮换)。</summary>
    public bool IsCurrent(VaultSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate) return IsCurrentNoLock(lease);
    }

    /// <summary>
    /// Verifies that a persisted session and a live lease describe the same current session.
    /// </summary>
    public bool IsCurrent(VaultSessionLease lease, PersistedSession persisted)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(persisted);
        lock (_gate)
        {
            return IsCurrentNoLock(lease)
                && persisted.HasValidBinding
                && persisted.AccountId == lease.AccountId
                && persisted.Endpoints == lease.Endpoints
                && persisted.Generation == lease.Generation;
        }
    }

    public bool IsUnlocked(VaultSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
            return IsCurrentNoLock(lease)
                && State == VaultState.Unlocked
                && _userKey is not null;
    }

    public bool TryRunIfUnlocked(VaultSessionLease lease, Action action)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease)
                || State != VaultState.Unlocked
                || _userKey is null)
                return false;
            action();
            return true;
        }
    }

    /// <summary>租约已失效时抛出 <see cref="OperationCanceledException"/>,携带该租约的取消令牌。</summary>
    public void ThrowIfExpired(VaultSessionLease lease)
    {
        if (!IsCurrent(lease))
            throw new OperationCanceledException("Vault session lease expired.", lease.CancellationToken);
    }

    /// <summary>
    /// 在同一 _gate 内校验租约仍然当前、再执行 mutation——校验和变更之间不留可被并发 Lock/Logout/
    /// Switch 插入的窗口。租约已失效时返回 false 且不执行 mutation。
    /// </summary>
    public bool TryCommit(VaultSessionLease lease, Action mutation)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease))
                return false;
            mutation();
            return true;
        }
    }

    public bool TryMarkDataStale(VaultSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease) || _userKey is null)
                return false;
            DataStale = true;
            return true;
        }
    }

    public bool TryMarkDataFresh(VaultSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease) || _userKey is null)
                return false;
            DataStale = false;
            return true;
        }
    }

    /// <summary>租约仍当前时返回 UserKey 的独立拷贝(调用方拥有其生命周期),否则返回 false。</summary>
    public bool TryCopyUserKey(VaultSessionLease lease, out SymmetricCryptoKey? key)
    {
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease) || _userKey?.Legacy is not { } legacy)
            {
                key = null;
                return false;
            }
            key = new SymmetricCryptoKey(legacy.FullKey.ToArray());
            return true;
        }
    }

    /// <summary>Returns an owned copy of the active V1 or V2 user key.</summary>
    public bool TryCopyUserKeyMaterial(VaultSessionLease lease, out VaultUserKey? key)
    {
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease) || _userKey is null)
            {
                key = null;
                return false;
            }

            key = _userKey.Copy();
            return true;
        }
    }

    /// <summary>
    /// Returns the encrypted private-key snapshot only while the lease remains current and unlocked.
    /// </summary>
    public bool TryGetEncryptedPrivateKey(
        VaultSessionLease lease,
        out string? encryptedPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease)
                || State != VaultState.Unlocked
                || _userKey is null)
            {
                encryptedPrivateKey = null;
                return false;
            }

            encryptedPrivateKey = EncryptedPrivateKey;
            return true;
        }
    }

    /// <summary>租约仍当前且已有 access token 时,返回绑定 account/origin/generation 的令牌快照。</summary>
    public AccessTokenBinding? GetAccessTokenBinding(VaultSessionLease lease)
    {
        lock (_gate)
        {
            return IsCurrentNoLock(lease) && !string.IsNullOrWhiteSpace(AccessToken)
                ? new AccessTokenBinding(lease.AccountId, lease.Endpoints, lease.Generation, AccessToken)
                : null;
        }
    }

    /// <summary>在同一 gate 内校验租约后返回条目快照(copy-on-write),租约失效则抛出取消异常。</summary>
    public IReadOnlyList<Cipher> GetCiphers(VaultSessionLease lease)
    {
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease))
                throw new OperationCanceledException(
                    "Vault session lease expired.",
                    lease.CancellationToken);
            return _ciphers;
        }
    }

    /// <summary>租约仍当前时返回 access/refresh token 快照,否则返回 false 且置空(不泄露过期令牌)。</summary>
    public bool TryGetTokenSnapshot(
        VaultSessionLease lease,
        out string? accessToken,
        out string? refreshToken)
    {
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease))
            {
                accessToken = null;
                refreshToken = null;
                return false;
            }
            accessToken = AccessToken;
            refreshToken = RefreshToken;
            return true;
        }
    }

    internal SecurityStampComparison CompareSecurityStamp(
        VaultSessionLease lease,
        string securityStamp)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(securityStamp);
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease))
                return SecurityStampComparison.StaleLease;

            var binding = (lease.AccountId, lease.Endpoints);
            if (!_securityStamps.TryGetValue(binding, out var current))
                return SecurityStampComparison.NoBaseline;
            return string.Equals(current, securityStamp, StringComparison.Ordinal)
                ? SecurityStampComparison.Match
                : SecurityStampComparison.Mismatch;
        }
    }

    internal bool TryCommitSecurityStamp(
        VaultSessionLease lease,
        string securityStamp) =>
        TryCommitSecurityStamp(lease, securityStamp, static () => { });

    internal bool TryCommitSecurityStamp(
        VaultSessionLease lease,
        string securityStamp,
        Action mutation)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(securityStamp);
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease))
                return false;

            var binding = (lease.AccountId, lease.Endpoints);
            if (_securityStamps.TryGetValue(binding, out var current)
                && !string.Equals(current, securityStamp, StringComparison.Ordinal))
            {
                return false;
            }

            mutation();

            SetSecurityStampNoLock(binding, securityStamp);
            return true;
        }
    }

    /// <summary>
    /// 锁定:第一动作即轮换 generation 并取消旧租约,再清空敏感状态。仍有活跃账户时返回新租约,
    /// 否则(未曾开启任何账户会话)返回 null。
    /// try/finally 保证即便 RotateLeaseNoLock 意外抛出，ClearSensitiveState 也必然执行，
    /// 安全清理路径不得 fail-open。
    /// </summary>
    public VaultSessionLease? Lock()
    {
        lock (_gate)
        {
            var accountId = _accountId;
            var endpoints = _endpoints;
            try
            {
                RotateAuthenticationAttemptNoLock(createCurrent: false);
                RotateLeaseNoLock(accountId, endpoints, persistedGeneration: null);
                return accountId is null || endpoints is null ? null : CurrentLeaseNoLock();
            }
            finally
            {
                ClearSensitiveState();
                State = VaultState.Locked;
            }
        }
    }

    /// <summary>
    /// 仅当 lease 仍对应当前会话时，rotation-first 锁定：先轮换 generation（使旧 lease 失效），
    /// 再清空敏感状态。用于 invalid_grant 场景——陈旧 lease 的刷新响应不会锁定已切换的新会话。
    /// 检查与锁定在同一 _gate 下，无 TOCTOU 窗口；try/finally 保证安全路径不得 fail-open。
    /// </summary>
    public bool TryLockIfCurrent(VaultSessionLease lease) =>
        TryLockIfCurrent(lease, out _);

    public bool TryLockIfCurrent(
        VaultSessionLease lease,
        out VaultSessionLease? lockedLease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (!IsCurrentNoLock(lease))
            {
                lockedLease = null;
                return false;
            }
            var accountId = _accountId;
            var endpoints = _endpoints;
            try
            {
                RotateAuthenticationAttemptNoLock(createCurrent: false);
                RotateLeaseNoLock(accountId, endpoints, persistedGeneration: null);
                lockedLease = CurrentLeaseNoLock();
            }
            finally
            {
                ClearSensitiveState();
                State = VaultState.Locked;
            }
            return true;
        }
    }

    /// <summary>登出:第一动作即轮换 generation(账户置空)并取消旧租约,再清空敏感状态。
    /// try/finally 保证即便 RotateLeaseNoLock 意外抛出，ClearSensitiveState、AccountInfo.Empty、
    /// LoggedOut 状态也必然被应用，安全清理路径不得 fail-open。</summary>
    public void Logout()
    {
        lock (_gate)
        {
            var accountId = _accountId;
            var endpoints = _endpoints;
            try
            {
                RotateAuthenticationAttemptNoLock(createCurrent: false);
                RotateLeaseNoLock(null, null, persistedGeneration: null);
            }
            finally
            {
                if (accountId is not null && endpoints is not null)
                {
                    RemoveSecurityStampNoLock((accountId, endpoints));
                }
                ClearSensitiveState();
                Account = AccountInfo.Empty;
                State = VaultState.LoggedOut;
            }
        }
    }

    private void BeginAccountSessionNoLock(
        string accountId,
        string email,
        ServerEndpoints endpoints)
    {
        RotateLeaseNoLock(accountId, endpoints, persistedGeneration: null);
        RemoveSecurityStampNoLock((accountId, endpoints));
        ClearSensitiveState();
        Account = new AccountInfo(
            accountId,
            email,
            endpoints,
            string.IsNullOrWhiteSpace(email) ? string.Empty : email.Trim()[..1].ToUpperInvariant(),
            string.Empty);
        State = VaultState.Unlocking;
    }

    private void RestorePersistedSessionNoLock(PersistedSession persisted)
    {
        var binding = (persisted.AccountId, persisted.Endpoints!);
        if (_accountId == persisted.AccountId
            && _endpoints == persisted.Endpoints
            && _generation == persisted.Generation
            && _leaseCts is { IsCancellationRequested: false })
        {
            TouchSecurityStampNoLock(binding);
            return;
        }

        RotateLeaseNoLock(persisted.AccountId, persisted.Endpoints!, persisted.Generation);
        TouchSecurityStampNoLock(binding);
        ClearSensitiveState();
        Account = new AccountInfo(
            persisted.AccountId,
            persisted.Email,
            persisted.Endpoints,
            persisted.Email.Trim()[..1].ToUpperInvariant(),
            string.Empty);
        State = VaultState.Locked;
    }

    private void SetSecurityStampNoLock(
        (string AccountId, ServerEndpoints Endpoints) binding,
        string securityStamp)
    {
        if (!_securityStamps.ContainsKey(binding)
            && _securityStamps.Count >= PersistedSessionCollection.MaximumSessions)
        {
            var oldest = _securityStampRecency.First
                ?? throw new InvalidOperationException("Security stamp recency state is inconsistent.");
            _securityStamps.Remove(oldest.Value);
            _securityStampRecency.RemoveFirst();
        }

        _securityStamps[binding] = securityStamp;
        TouchSecurityStampNoLock(binding);
    }

    private void TouchSecurityStampNoLock(
        (string AccountId, ServerEndpoints Endpoints) binding)
    {
        if (!_securityStamps.ContainsKey(binding))
            return;
        _securityStampRecency.Remove(binding);
        _securityStampRecency.AddLast(binding);
    }

    private void RemoveSecurityStampNoLock(
        (string AccountId, ServerEndpoints Endpoints) binding)
    {
        _securityStamps.Remove(binding);
        _securityStampRecency.Remove(binding);
    }

    private void RotateAuthenticationAttemptNoLock(bool createCurrent)
    {
        var previous = _authenticationCts;
        _authenticationGeneration = _authenticationGeneration == long.MaxValue
            ? long.MaxValue
            : _authenticationGeneration + 1;
        _authenticationStamp = Guid.NewGuid();
        _authenticationCts = createCurrent ? new CancellationTokenSource() : null;

        if (previous is null)
            return;

        try
        {
            previous.Cancel();
        }
        catch (AggregateException)
        {
            // A cancellation callback cannot preserve a superseded attempt.
        }
        finally
        {
            previous.Dispose();
        }
    }

    private bool IsCurrentNoLock(AuthenticationAttemptLease attempt) =>
        _authenticationCts is { IsCancellationRequested: false }
        && attempt.Generation == _authenticationGeneration
        && attempt.Stamp == _authenticationStamp;

    private AuthenticationAttemptLease CurrentAuthenticationAttemptNoLock() => new(
        _authenticationGeneration,
        _authenticationCts?.Token
            ?? throw new InvalidOperationException("No authentication attempt is active."),
        _authenticationStamp);

    /// <summary>
    /// 唯一的 generation/stamp/CTS 变更点。必须在 _gate 内调用。取消旧 CTS 在赋新值之后执行,
    /// 确保"generation 已经推进"先于"旧 token 被观察到取消"对外可见,任何取消回调异常都不得
    /// 阻断新会话生效(仅吞掉 AggregateException,不吞其他致命异常)。
    /// generation 计数采用饱和而非 checked 溢出：到达 long.MaxValue 后保持在 long.MaxValue，
    /// 同时每次仍轮换 stamp 与 CTS，确保旧 lease 必然失效。long.MaxValue 值本身被
    /// PersistedSession.HasValidBinding 拒绝（Generation > MaxPersistedGeneration），
    /// 故该状态不可通过 RestorePersistedSession 再次被持久化注入，属最终不可达饱和态。
    /// </summary>
    private void RotateLeaseNoLock(
        string? accountId,
        ServerEndpoints? endpoints,
        long? persistedGeneration)
    {
        var previous = _leaseCts;
        // 饱和加一：不使用 checked，到达 long.MaxValue 后保持不变（不溢出）。
        var next = _generation == long.MaxValue ? long.MaxValue : _generation + 1;
        _generation = persistedGeneration is long restored
            ? Math.Max(next, restored)
            : next;
        _accountId = accountId;
        _endpoints = endpoints;
        _stamp = Guid.NewGuid();
        _leaseCts = accountId is null || endpoints is null
            ? null
            : new CancellationTokenSource();

        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (AggregateException)
            {
                // A cancellation callback cannot prevent generation invalidation.
            }
            finally
            {
                previous.Dispose();
            }
        }
    }

    private bool IsCurrentNoLock(VaultSessionLease lease) =>
        _leaseCts is { IsCancellationRequested: false }
        && lease.Generation == _generation
        && lease.AccountId == _accountId
        && lease.Endpoints == _endpoints
        && lease.Stamp == _stamp;

    private VaultSessionLease CurrentLeaseNoLock() => new(
        _generation,
        _accountId ?? throw new InvalidOperationException("No account is active."),
        _endpoints ?? throw new InvalidOperationException("No server endpoints are active."),
        _leaseCts?.Token ?? throw new InvalidOperationException("No lease token is active."),
        _stamp);

    private void ClearSensitiveState()
    {
        // The discriminated owner clears all backing arrays for either V1 or V2 user keys.
        _userKey?.Dispose();

        // 【已知残余风险 — 托管明文残留】锁定/登出这里只能清零 UserKey 的字节数组(可变 byte[],
        // 可就地 ZeroMemory)。而解密后的 Cipher 字段明文(用户名/密码/备注等)是不可变的
        // System.String,CLR 不提供就地清零手段:即便把引用置空(下方 _ciphers = Empty),
        // 明文字符串仍作为托管堆对象存在,直到 GC 回收并压缩堆之前一直残留在内存中。
        // 因此锁定后、下一次 GC 前的内存转储仍可能包含明文。这是托管运行时的固有限制,
        // Bitwarden 官方桌面客户端(基于同类托管/JS 运行时)也存在同样约束,属已知残余风险。
        // (如需进一步缓解可在锁定后 GC.Collect(),但不能根除,本批不引入。)
        AccessToken = null;
        RefreshToken = null;
        _userKey = null;
        _ciphers = Array.Empty<Cipher>();
        _folders = Array.Empty<Folder>();
        _devices = Array.Empty<DeviceInfo>();
        _sends = Array.Empty<Send>();
        _organizations = Array.Empty<VaultOrganization>();
        _collections = Array.Empty<VaultCollection>();
        _policies = Array.Empty<VaultOrganizationPolicy>();
        _policyOrganizations = Array.Empty<VaultOrganizationPolicyContext>();
        _domains = null;
        _capabilities = VaultCapabilities.None;
        IsDemo = false;
        EncryptedPrivateKey = null;
        SkippedCipherCount = 0;
        SkippedFolderCount = 0;
        SkippedSendCount = 0;
        UnsupportedOrganizationCipherCount = 0;
        UnsupportedProviderOrganizationCount = 0;
        DataStale = false;
    }
}

internal enum SecurityStampComparison
{
    StaleLease,
    NoBaseline,
    Match,
    Mismatch,
}
