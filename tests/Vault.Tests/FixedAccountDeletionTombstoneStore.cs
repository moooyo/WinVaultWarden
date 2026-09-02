using Core.Abstractions;
using Core.Models;

namespace Vault.Tests;

internal sealed class FixedAccountDeletionTombstoneStore(
    AccountDeletionTombstoneReadAllResult readAll) : IAccountDeletionTombstoneStore
{
    public int ReadCount { get; private set; }

    public AccountDeletionTombstoneReadResult Read(AccountDeletionBinding binding)
    {
        var all = ReadAll();
        if (all.Status == AccountDeletionTombstoneReadStatus.Corrupt)
            return AccountDeletionTombstoneReadResult.Corrupt;
        if (all.Status == AccountDeletionTombstoneReadStatus.Unavailable)
            return AccountDeletionTombstoneReadResult.Unavailable;
        var record = all.Records.SingleOrDefault(candidate => candidate.Binding == binding);
        return record is null
            ? AccountDeletionTombstoneReadResult.Absent
            : AccountDeletionTombstoneReadResult.Found(record);
    }

    public AccountDeletionTombstoneReadAllResult ReadAll()
    {
        ReadCount++;
        return readAll;
    }

    public AccountDeletionTombstoneRecord? TryCreate(
        AccountDeletionBinding binding,
        DateTimeOffset createdAt) => throw new NotSupportedException();
    public AccountDeletionTombstoneRecord? TryAdvance(
        AccountDeletionTombstoneRecord expected,
        AccountDeletionStage nextStage,
        DateTimeOffset updatedAt) => throw new NotSupportedException();
    public bool Remove(
        AccountDeletionTombstoneRecord expected,
        AccountDeletionTombstoneRemovalReason reason) => throw new NotSupportedException();

    public static FixedAccountDeletionTombstoneStore ForSession(
        PersistedSession session)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new AccountDeletionTombstoneRecord(
            Guid.NewGuid(),
            1,
            now,
            now,
            new AccountDeletionBinding(
                session.AccountId,
                session.Email,
                session.Endpoints!,
                "11111111-1111-1111-1111-111111111111"),
            AccountDeletionStage.Prepared);
        return new FixedAccountDeletionTombstoneStore(
            AccountDeletionTombstoneReadAllResult.Found([record]));
    }
}
