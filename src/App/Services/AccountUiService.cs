using Core.Models;
using Core.Services;

namespace App.Services;

public interface IAccountUiService
{
    AccountInfo GetAccount();
}

public sealed class AccountUiService : IAccountUiService
{
    private readonly IVaultService _vault;

    public AccountUiService(IVaultService vault) => _vault = vault;

    public AccountInfo GetAccount() => _vault.Snapshot.Account;
}
