using Core.Abstractions;
using Core.Models;
using Core.Services;

namespace App.Services;

/// <summary>
/// Holds the global deletion gate from the durable tombstone check through the caller's complete
/// account-bound store write.
/// </summary>
public sealed class AccountDeletionLocalWriteGuard : IAccountDeletionLocalWriteGuard
{
    private readonly IAccountDeletionCoordinationGate _coordinationGate;
    private readonly IAccountDeletionTombstoneStore _tombstones;

    public AccountDeletionLocalWriteGuard(
        IAccountDeletionCoordinationGate coordinationGate,
        IAccountDeletionTombstoneStore tombstones)
    {
        _coordinationGate = coordinationGate
            ?? throw new ArgumentNullException(nameof(coordinationGate));
        _tombstones = tombstones ?? throw new ArgumentNullException(nameof(tombstones));
    }

    public IDisposable? TryAcquire(string accountId, ServerEndpoints endpoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);

        IAccountDeletionCoordinationLease? coordination = null;
        try
        {
            coordination = _coordinationGate.TryAcquire();
            if (coordination is null)
                return null;

            var read = _tombstones.ReadAll();
            var allowed = read.Status switch
            {
                AccountDeletionTombstoneReadStatus.Absent => true,
                AccountDeletionTombstoneReadStatus.Found => !read.Records.Any(record =>
                    string.Equals(record.Binding.AccountId, accountId, StringComparison.Ordinal)
                    && record.Binding.Endpoints == endpoints),
                _ => false,
            };
            if (!allowed)
                return null;

            var acquired = coordination;
            coordination = null;
            return acquired;
        }
        catch
        {
            return null;
        }
        finally
        {
            coordination?.Dispose();
        }
    }
}

internal static class AccountDeletionLocalWriteGuardExtensions
{
    public static IDisposable AcquireOrThrow(
        this IAccountDeletionLocalWriteGuard guard,
        string accountId,
        ServerEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(guard);
        try
        {
            return guard.TryAcquire(accountId, endpoints)
                ?? throw new AccountDeletionWriteBlockedException();
        }
        catch (AccountDeletionWriteBlockedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AccountDeletionWriteBlockedException(exception);
        }
    }
}

internal sealed class AllowAllAccountDeletionLocalWriteGuard
    : IAccountDeletionLocalWriteGuard
{
    public static AllowAllAccountDeletionLocalWriteGuard Instance { get; } = new();

    private AllowAllAccountDeletionLocalWriteGuard()
    {
    }

    public IDisposable TryAcquire(string accountId, ServerEndpoints endpoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);
        return NoopLease.Instance;
    }

    private sealed class NoopLease : IDisposable
    {
        public static NoopLease Instance { get; } = new();

        private NoopLease()
        {
        }

        public void Dispose()
        {
        }
    }
}
