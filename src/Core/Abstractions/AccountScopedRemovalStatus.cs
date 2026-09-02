using Core.Models;

namespace Core.Abstractions;

public enum AccountScopedRemovalStatus
{
    Removed,
    Absent,
    Corrupt,
    Unavailable,
}

internal static class AccountScopedRemovalValidation
{
    public static void Validate(string accountId, ServerEndpoints endpoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);
    }
}
