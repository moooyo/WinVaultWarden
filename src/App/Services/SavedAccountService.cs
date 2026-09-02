using Core.Abstractions;
using Core.Models;
using Core.Services;

namespace App.Services;

public sealed record SavedAccountOption(
    string AccountId,
    string Email,
    ServerEndpoints Endpoints,
    bool IsActive)
{
    public string ServerUrl => Endpoints.WebVault.Value;
    public string Initial => string.IsNullOrWhiteSpace(Email)
        ? "?"
        : Email.Trim()[..1].ToUpperInvariant();
}

public interface ISavedAccountService
{
    IReadOnlyList<SavedAccountOption> GetAccounts();
    bool TryActivate(SavedAccountOption account);
}

public sealed class SavedAccountService : ISavedAccountService
{
    private readonly ITokenStore _tokenStore;
    private readonly IAccountDeletionService? _accountDeletion;

    public SavedAccountService(ITokenStore tokenStore)
    {
        _tokenStore = tokenStore;
    }

    public SavedAccountService(
        ITokenStore tokenStore,
        IAccountDeletionService accountDeletion)
    {
        _tokenStore = tokenStore;
        _accountDeletion = accountDeletion;
    }

    public IReadOnlyList<SavedAccountOption> GetAccounts()
    {
        if (!TryReadPending(out var pending))
            return [];

        try
        {
            return _tokenStore.GetSavedAccounts()
                .Where(account => !IsTombstoned(
                    account.AccountId,
                    account.Endpoints,
                    pending))
                .Select(account => new SavedAccountOption(
                    account.AccountId,
                    account.Email,
                    account.Endpoints,
                    account.IsActive))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public bool TryActivate(SavedAccountOption account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!TryReadPending(out var pending)
            || IsTombstoned(account.AccountId, account.Endpoints, pending))
        {
            return false;
        }

        try
        {
            return _tokenStore.TryActivate(
                account.AccountId,
                account.Endpoints,
                out _);
        }
        catch
        {
            return false;
        }
    }

    private bool TryReadPending(
        out IReadOnlyList<AccountDeletionTombstoneRecord> pending)
    {
        try
        {
            if (_accountDeletion is null)
            {
                pending = [];
                return true;
            }

            var read = _accountDeletion.ReadPending();
            switch (read.Status)
            {
                case AccountDeletionTombstoneReadStatus.Absent:
                    pending = [];
                    return true;
                case AccountDeletionTombstoneReadStatus.Found:
                    pending = read.Records;
                    return true;
                default:
                    pending = [];
                    return false;
            }
        }
        catch
        {
            pending = [];
            return false;
        }
    }

    private static bool IsTombstoned(
        string accountId,
        ServerEndpoints endpoints,
        IReadOnlyList<AccountDeletionTombstoneRecord> pending) =>
        pending.Any(record =>
            string.Equals(
                record.Binding.AccountId,
                accountId,
                StringComparison.Ordinal)
            && record.Binding.Endpoints == endpoints);
}
