using Core.Abstractions;
using Core.Models;
using Core.Services;

namespace App.Services;

public sealed class AccountDeletionRecoveryHost(
    IAccountDeletionService accountDeletion)
{
    private readonly object _failureGate = new();
    private readonly List<AccountDeletionTombstoneRecord> _failedCleanupRecords = [];

    public IReadOnlyList<AccountDeletionTombstoneRecord> FailedCleanupRecords
    {
        get
        {
            lock (_failureGate)
                return Array.AsReadOnly(_failedCleanupRecords.ToArray());
        }
    }

    public void MarkResolved(AccountDeletionTombstoneRecord expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        lock (_failureGate)
            _failedCleanupRecords.RemoveAll(record => record == expected);
    }

    public async Task ResumeConfirmedCleanupAsync(
        CancellationToken ct = default)
    {
        ResetFailures();
        AccountDeletionTombstoneReadAllResult read;
        try
        {
            read = accountDeletion.ReadPending();
        }
        catch
        {
            return;
        }

        if (read.Status != AccountDeletionTombstoneReadStatus.Found)
            return;

        foreach (var pending in read.Records)
        {
            ct.ThrowIfCancellationRequested();
            if (pending.Stage is not (
                    AccountDeletionStage.ServerConfirmed
                    or AccountDeletionStage.LocalCleanupComplete
                    or AccountDeletionStage.UncertainLocalCleanupComplete))
                continue;

            try
            {
                var result = await accountDeletion.ResumeConfirmedCleanupAsync(pending, ct);
                if (IsSuccessfulCleanup(pending, result))
                    MarkResolved(pending);
                else
                    MarkFailed(FailureRecord(result, pending));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                MarkFailed(pending);
                throw;
            }
            catch
            {
                MarkFailed(pending);
                // One damaged account binding must not prevent recovery of the remaining records.
            }
        }
    }

    private void ResetFailures()
    {
        lock (_failureGate)
            _failedCleanupRecords.Clear();
    }

    private void MarkFailed(AccountDeletionTombstoneRecord pending)
    {
        lock (_failureGate)
        {
            _failedCleanupRecords.RemoveAll(record => SameScope(record, pending));
            _failedCleanupRecords.Add(pending);
        }
    }

    private static bool IsSuccessfulCleanup(
        AccountDeletionTombstoneRecord expected,
        AccountDeletionResult result) => expected.Stage switch
        {
            AccountDeletionStage.ServerConfirmed
                or AccountDeletionStage.LocalCleanupComplete =>
                result is AccountDeletionResult.Confirmed,
            AccountDeletionStage.UncertainLocalCleanupComplete =>
                result is AccountDeletionResult.OutcomeUncertainLocalCleanupComplete complete
                && complete.Pending == expected,
            _ => false,
        };

    private static AccountDeletionTombstoneRecord FailureRecord(
        AccountDeletionResult result,
        AccountDeletionTombstoneRecord fallback)
    {
        var candidate = result switch
        {
            AccountDeletionResult.OutcomeUncertainLocalCleanupComplete complete =>
                complete.Pending,
            AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete incomplete =>
                incomplete.Pending,
            AccountDeletionResult.ConfirmedLocalCleanupIncomplete incomplete =>
                incomplete.Pending,
            AccountDeletionResult.OutcomeUncertain uncertain => uncertain.Pending,
            AccountDeletionResult.Rejected { Pending: not null } rejected => rejected.Pending,
            AccountDeletionResult.CancelledBeforeDispatch { Pending: not null } cancelled =>
                cancelled.Pending,
            _ => fallback,
        };
        return SameScope(candidate, fallback) ? candidate : fallback;
    }

    private static bool SameScope(
        AccountDeletionTombstoneRecord left,
        AccountDeletionTombstoneRecord right) =>
        string.Equals(
            left.Binding.AccountId,
            right.Binding.AccountId,
            StringComparison.Ordinal)
        && left.Binding.Endpoints == right.Binding.Endpoints;
}
