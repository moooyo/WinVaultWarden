using Core.Services;
using Vault;

namespace App.Services;

// 条目级访问策略:某条目是否要求主密码重提示。由 IVaultUiService 从当前快照读出,授权判定的唯一输入。
public readonly record struct CipherAccessPolicy(
    string CipherId,
    bool RequiresReprompt);

// 触发条目级门禁的敏感动作类型。揭示/复制/TOTP/编辑/下载/打开附件/使用 passkey 共用同一门禁。
public enum SensitiveItemAction
{
    Reveal,
    Copy,
    Totp,
    Edit,
    DownloadAttachment,
    OpenAttachment,
    UsePasskey,
}

// 条目级主密码重提示门禁。授权缓存键为 (lease.Generation, lease.AccountId, cipherId):
// Lock/登出/切账户都会轮换 generation(并经 ISensitiveStateRegistry 调用 ClearSensitive),
// 故任何会话身份变化都会立即失效既有授权。无活跃会话/异常/取消一律 fail-closed(拒绝)。
public interface IItemRepromptGate : ISensitiveState
{
    bool IsAuthorized(CipherAccessPolicy policy);
    Task<UiOperationResult<bool>> AuthorizeAsync(
        CipherAccessPolicy policy,
        SensitiveItemAction action,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default);
}

public sealed class ItemRepromptGate(
    IAccountService account,
    VaultSession session) : IItemRepromptGate
{
    private readonly object _gate = new();
    // 缓存键为 CipherId，值为授权时捕获的完整 VaultSessionLease（含 stamp）。
    // IsAuthorized 必须调用 session.IsCurrent(cachedLease) 而非仅比较 generation/account，
    // 以保证在 generation 饱和（long.MaxValue）时每次 Lock 轮换的 stamp 仍能正确失效授权。
    // 注意：session.IsCurrent 获取 VaultSession 自身锁，故必须在释放 _gate 后再调用，避免锁序死锁。
    private readonly Dictionary<string, Core.Session.VaultSessionLease> _authorized = [];

    public bool IsAuthorized(CipherAccessPolicy policy)
    {
        // 非重提示条目无需门禁。
        if (!policy.RequiresReprompt)
            return true;

        Core.Session.VaultSessionLease? cached;
        lock (_gate)
            _authorized.TryGetValue(policy.CipherId, out cached);

        if (cached is null)
            return false;

        // 释放 _gate 后再调用 IsCurrent，避免与 VaultSession 内锁产生死锁。
        if (session.IsCurrent(cached))
            return true;

        // 失效 entry 可移除，节省内存；只在值未被其他线程替换时才移除（防 TOCTOU）。
        lock (_gate)
            if (_authorized.TryGetValue(policy.CipherId, out var current)
                && ReferenceEquals(current, cached))
                _authorized.Remove(policy.CipherId);

        return false;
    }

    public async Task<UiOperationResult<bool>> AuthorizeAsync(
        CipherAccessPolicy policy,
        SensitiveItemAction action,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default)
    {
        if (!policy.RequiresReprompt || IsAuthorized(policy))
            return UiOperationResult<bool>.Success(true);

        Core.Session.VaultSessionLease lease;
        try
        {
            lease = session.CaptureLease();
        }
        catch
        {
            // 无活跃会话 → 无法验证 → 拒绝。
            return UiOperationResult<bool>.Failure("敏感条目验证会话不可用");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            lease.CancellationToken);

        try
        {
            var password = await prompt(linked.Token);
            if (password is null)
                return UiOperationResult<bool>.Cancelled();
            if (!await account.VerifyMasterPasswordAsync(password, linked.Token))
                return UiOperationResult<bool>.Failure("主密码不正确");

            // 跨 await 后校验仍为同一会话租约（含 stamp 比较）：期间 Lock/切账户则授权作废。
            if (!session.IsCurrent(lease))
                return UiOperationResult<bool>.Cancelled();

            // 缓存完整 lease（含 stamp），后续 IsAuthorized 通过 IsCurrent 验证其有效性。
            lock (_gate)
                _authorized[policy.CipherId] = lease;
            return UiOperationResult<bool>.Success(true);
        }
        catch (OperationCanceledException)
        {
            // 验证期间会话被锁/取消 → 绝不缓存授权。
            return UiOperationResult<bool>.Cancelled();
        }
    }

    public void ClearSensitive()
    {
        lock (_gate)
            _authorized.Clear();
    }
}
