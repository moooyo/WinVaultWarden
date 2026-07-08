namespace App.Services;

// 主密码重提示门禁(会话级)。判定"本次解锁会话是否已通过主密码验证",并校验主密码;
// 会话级标志由 VaultSession.RepromptSatisfied 承载(锁定/登出即复位)。纯 net 抽象,可被 App.Tests 链接。
public interface IMasterPasswordReprompt
{
    // 本次解锁会话是否已通过主密码重提示验证。
    bool IsSessionVerified { get; }

    // 校验主密码;通过则置会话级已验证标志并返回 true,否则返回 false(不抛)。
    Task<bool> VerifyAsync(string password, CancellationToken ct = default);
}
