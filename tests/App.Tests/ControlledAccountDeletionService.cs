using Core.Abstractions;
using Core.Models;
using Core.Services;
using Core.Session;

namespace App.Tests;

internal sealed class ControlledAccountDeletionService : IAccountDeletionService
{
    public AccountDeletionTombstoneReadAllResult ReadAllResult { get; set; } =
        AccountDeletionTombstoneReadAllResult.Absent;

    public bool ThrowOnRead { get; set; }

    public Func<AccountDeletionTombstoneRecord, CancellationToken,
        Task<AccountDeletionResult>>? ResumeHandler { get; set; }

    public Func<AccountDeletionTombstoneRecord, CancellationToken,
        Task<AccountDeletionLocalResolutionResult>>? ClearHandler { get; set; }

    public List<AccountDeletionTombstoneRecord> ResumeCalls { get; } = [];
    public List<AccountDeletionTombstoneRecord> ClearCalls { get; } = [];

    public AccountDeletionPreflight GetPreflight() =>
        throw new NotSupportedException();

    public Task<AccountDeletionOtpRequestResult> RequestOtpAsync(
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task<AccountDeletionResult> DeleteAsync(
        AccountDeletionCredential credential,
        CancellationToken ct = default) => throw new NotSupportedException();

    public AccountDeletionTombstoneReadResult ReadPending(
        AccountDeletionBinding binding)
    {
        var all = ReadPending();
        if (all.Status == AccountDeletionTombstoneReadStatus.Corrupt)
            return AccountDeletionTombstoneReadResult.Corrupt;
        if (all.Status == AccountDeletionTombstoneReadStatus.Unavailable)
            return AccountDeletionTombstoneReadResult.Unavailable;
        var record = all.Records.SingleOrDefault(candidate =>
            candidate.Binding == binding);
        return record is null
            ? AccountDeletionTombstoneReadResult.Absent
            : AccountDeletionTombstoneReadResult.Found(record);
    }

    public AccountDeletionTombstoneReadAllResult ReadPending()
    {
        if (ThrowOnRead)
            throw new IOException("read failed");
        return ReadAllResult;
    }

    public Task<AccountDeletionResult> ResumeConfirmedCleanupAsync(
        AccountDeletionTombstoneRecord expected,
        CancellationToken ct = default)
    {
        ResumeCalls.Add(expected);
        if (ResumeHandler is not null)
            return ResumeHandler(expected, ct);
        var completed = AdvanceToCompletedMarker(expected);
        return Task.FromResult<AccountDeletionResult>(
            expected.Stage == AccountDeletionStage.UncertainLocalCleanupComplete
                ? new AccountDeletionResult.OutcomeUncertainLocalCleanupComplete(
                    completed)
                : new AccountDeletionResult.Confirmed(null));
    }

    public Task<AccountDeletionLocalResolutionResult>
        ClearLocalDataForPendingAsync(
            AccountDeletionTombstoneRecord expected,
            CancellationToken ct = default)
    {
        ClearCalls.Add(expected);
        if (ClearHandler is not null)
            return ClearHandler(expected, ct);
        var completed = AdvanceToCompletedMarker(expected);
        return Task.FromResult(new AccountDeletionLocalResolutionResult(
            AccountDeletionLocalResolutionStatus.Completed,
            completed));
    }

    private AccountDeletionTombstoneRecord AdvanceToCompletedMarker(
        AccountDeletionTombstoneRecord expected)
    {
        if (ReadAllResult.Status != AccountDeletionTombstoneReadStatus.Found)
            return expected;

        var nextStage = expected.Stage switch
        {
            AccountDeletionStage.Prepared =>
                AccountDeletionStage.UncertainLocalCleanupComplete,
            AccountDeletionStage.ServerConfirmed =>
                AccountDeletionStage.LocalCleanupComplete,
            AccountDeletionStage.LocalCleanupComplete
                or AccountDeletionStage.UncertainLocalCleanupComplete => expected.Stage,
            _ => throw new ArgumentOutOfRangeException(nameof(expected)),
        };
        if (nextStage == expected.Stage)
            return expected;

        var complete = new AccountDeletionTombstoneRecord(
            expected.EntryId,
            checked(expected.Revision + 1),
            expected.CreatedAt,
            expected.UpdatedAt.AddTicks(1),
            expected.Binding,
            nextStage);
        ReadAllResult = AccountDeletionTombstoneReadAllResult.Found(
            ReadAllResult.Records
                .Select(record => record == expected ? complete : record)
                .ToArray());
        return complete;
    }
}
