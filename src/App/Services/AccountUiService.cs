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

    Task ChangeKdfAsync(string current, AccountKdfParameters parameters, CancellationToken ct = default) =>
        parameters.Type == Core.Enums.KdfType.Pbkdf2 && parameters.IsWriteCompatible
            ? ChangeIterationsAsync(current, parameters.Iterations, ct)
            : Task.FromException(new NotSupportedException("This account UI service cannot change KDF algorithms."));
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
            throw new AccountOperationException(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB742DBD6B37D5897));
        return _account.UpdateNameAsync(name, ct);
    }

    public Task ChangePasswordAsync(string current, string next, string confirm, string? hint, CancellationToken ct = default)
    {
        if (_account is null)
            throw new AccountOperationException(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB742DBD6B37D5897));
        if (string.IsNullOrEmpty(next))
            throw new AccountOperationException(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC90FD1B53F3523CC));
        if (next != confirm)
            throw new AccountOperationException(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC5575A2E4B0311B8));
        return _account.ChangePasswordAsync(current, next, hint, ct);
    }

    public Task ChangeIterationsAsync(string current, int iterations, CancellationToken ct = default)
    {
        if (_account is null)
            throw new AccountOperationException(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB742DBD6B37D5897));
        return _account.ChangeKdfAsync(current, iterations, ct);
    }

    public Task ChangeKdfAsync(string current, AccountKdfParameters parameters, CancellationToken ct = default)
    {
        if (_account is null)
            throw new AccountOperationException("The account service is unavailable.");
        return _account.ChangeKdfAsync(current, parameters, ct);
    }
}
