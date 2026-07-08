using Core.Services;
using Vault;

namespace App.Services;

// 复用 AccountService.VerifyMasterPasswordAsync 校验;通过后在 VaultSession 置会话级标志。
// 主密码绝不落盘/写日志:仅透传给校验后即由调用方释放。
public sealed class MasterPasswordReprompt : IMasterPasswordReprompt
{
    private readonly IAccountService _account;
    private readonly VaultSession _session;

    public MasterPasswordReprompt(IAccountService account, VaultSession session)
    {
        _account = account;
        _session = session;
    }

    public bool IsSessionVerified => _session.RepromptSatisfied;

    public async Task<bool> VerifyAsync(string password, CancellationToken ct = default)
    {
        var ok = await _account.VerifyMasterPasswordAsync(password, ct);
        if (ok)
            _session.MarkRepromptSatisfied();
        return ok;
    }
}
