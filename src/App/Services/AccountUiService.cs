using Core.Models;
using Core.Services;

namespace App.Services;

public interface IAccountUiService
{
    AccountInfo GetAccount();

    // 改名：委托到 IAccountService.UpdateNameAsync
    Task RenameAsync(string name, CancellationToken ct = default);

    // 改密：先做客户端校验（confirm 匹配、非空），再委托
    Task ChangePasswordAsync(string current, string next, string confirm, string? hint, CancellationToken ct = default);

    // 改迭代次数（KDF）
    Task ChangeIterationsAsync(string current, int iterations, CancellationToken ct = default);
}

public sealed class AccountUiService : IAccountUiService
{
    private readonly IVaultService _vault;
    private readonly IAccountService? _account;

    // 保持单参数构造器以兼容现有注册（无 IAccountService 时账户操作不可用）
    public AccountUiService(IVaultService vault, IAccountService? account = null)
    {
        _vault = vault;
        _account = account;
    }

    public AccountInfo GetAccount() => _vault.Snapshot.Account;

    public Task RenameAsync(string name, CancellationToken ct = default)
    {
        if (_account is null)
            throw new AccountOperationException("账户服务不可用");
        return _account.UpdateNameAsync(name, ct);
    }

    public Task ChangePasswordAsync(string current, string next, string confirm, string? hint, CancellationToken ct = default)
    {
        if (_account is null)
            throw new AccountOperationException("账户服务不可用");
        if (string.IsNullOrEmpty(next))
            throw new AccountOperationException("新密码不能为空");
        if (next != confirm)
            throw new AccountOperationException("两次输入的新密码不一致");
        return _account.ChangePasswordAsync(current, next, hint, ct);
    }

    public Task ChangeIterationsAsync(string current, int iterations, CancellationToken ct = default)
    {
        if (_account is null)
            throw new AccountOperationException("账户服务不可用");
        return _account.ChangeKdfAsync(current, iterations, ct);
    }
}
