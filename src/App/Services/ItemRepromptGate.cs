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
    private readonly HashSet<(long Generation, string AccountId, string CipherId)> _authorized = [];

    public bool IsAuthorized(CipherAccessPolicy policy)
    {
        // 非重提示条目无需门禁。重提示条目在无会话/异常时一律拒绝(fail-closed)。
        if (!policy.RequiresReprompt)
            return true;

        Core.Session.VaultSessionLease lease;
        try
        {
            lease = session.CaptureLease();
        }
        catch
        {
            return false;
        }

        lock (_gate)
            return _authorized.Contains((lease.Generation, lease.AccountId, policy.CipherId));
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

            // 跨 await 后必须校验仍为同一会话租约:期间 Lock/切账户则授权作废。
            if (!session.IsCurrent(lease))
                return UiOperationResult<bool>.Cancelled();

            lock (_gate)
                _authorized.Add((lease.Generation, lease.AccountId, policy.CipherId));
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
