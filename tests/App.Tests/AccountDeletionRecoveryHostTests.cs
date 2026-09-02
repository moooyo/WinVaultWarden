using App.Services;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Xunit;

namespace App.Tests;

public sealed class AccountDeletionRecoveryHostTests
{
    [Fact]
    public async Task StartupRecovery_ProcessesConfirmedAndCompleteMarkersAndContinuesAfterFailure()
    {
        var prepared = Pending("prepared", AccountDeletionStage.Prepared);
        var firstConfirmed = Pending("confirmed-a", AccountDeletionStage.ServerConfirmed);
        var complete = Pending("complete", AccountDeletionStage.LocalCleanupComplete);
        var uncertainComplete = Pending(
            "uncertain-complete",
            AccountDeletionStage.UncertainLocalCleanupComplete);
        var secondConfirmed = Pending("confirmed-b", AccountDeletionStage.ServerConfirmed);
        var deletion = new ControlledAccountDeletionService
        {
            ReadAllResult = AccountDeletionTombstoneReadAllResult.Found(
                [prepared, firstConfirmed, complete, uncertainComplete, secondConfirmed]),
            ResumeHandler = (record, _) => record == firstConfirmed
                ? Task.FromException<AccountDeletionResult>(
                    new IOException("first cleanup failed"))
                : record.Stage == AccountDeletionStage.UncertainLocalCleanupComplete
                    ? Task.FromResult<AccountDeletionResult>(
                        new AccountDeletionResult.OutcomeUncertainLocalCleanupComplete(
                            record))
                : Task.FromResult<AccountDeletionResult>(
                    new AccountDeletionResult.Confirmed(null)),
        };
        var host = new AccountDeletionRecoveryHost(deletion);

        await host.ResumeConfirmedCleanupAsync();

        Assert.Equal(
            [firstConfirmed, complete, uncertainComplete, secondConfirmed],
            deletion.ResumeCalls);
        Assert.DoesNotContain(prepared, deletion.ResumeCalls);
        Assert.Same(firstConfirmed, Assert.Single(host.FailedCleanupRecords));
    }

    [Fact]
    public async Task IncompleteCleanupResults_AreRetainedAsExactFailureRecords()
    {
        var confirmed = Pending("confirmed", AccountDeletionStage.LocalCleanupComplete);
        var uncertain = Pending(
            "uncertain",
            AccountDeletionStage.UncertainLocalCleanupComplete);
        var deletion = new ControlledAccountDeletionService
        {
            ReadAllResult = AccountDeletionTombstoneReadAllResult.Found(
                [confirmed, uncertain]),
            ResumeHandler = (record, _) => record == confirmed
                ? Task.FromResult<AccountDeletionResult>(
                    new AccountDeletionResult.ConfirmedLocalCleanupIncomplete(
                        record,
                        [Failure(AccountDeletionCleanupTarget.VaultCache)]))
                : Task.FromResult<AccountDeletionResult>(
                    new AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete(
                        record,
                        [Failure(AccountDeletionCleanupTarget.CipherCreatePending)])),
        };
        var host = new AccountDeletionRecoveryHost(deletion);

        await host.ResumeConfirmedCleanupAsync();

        Assert.Equal([confirmed, uncertain], host.FailedCleanupRecords);
    }

    [Fact]
    public async Task StageMismatchedSuccessResults_RemainFailures()
    {
        var confirmed = Pending("confirmed", AccountDeletionStage.LocalCleanupComplete);
        var uncertain = Pending(
            "uncertain",
            AccountDeletionStage.UncertainLocalCleanupComplete);
        var deletion = new ControlledAccountDeletionService
        {
            ReadAllResult = AccountDeletionTombstoneReadAllResult.Found(
                [confirmed, uncertain]),
            ResumeHandler = (record, _) => record == confirmed
                ? Task.FromResult<AccountDeletionResult>(
                    new AccountDeletionResult.OutcomeUncertainLocalCleanupComplete(
                        uncertain))
                : Task.FromResult<AccountDeletionResult>(
                    new AccountDeletionResult.Confirmed(null)),
        };
        var host = new AccountDeletionRecoveryHost(deletion);

        await host.ResumeConfirmedCleanupAsync();

        Assert.Equal([confirmed, uncertain], host.FailedCleanupRecords);
    }

    [Fact]
    public async Task MarkResolved_RemovesOnlyTheExactFailureFromReadOnlySnapshot()
    {
        var first = Pending("first", AccountDeletionStage.LocalCleanupComplete);
        var second = Pending("second", AccountDeletionStage.LocalCleanupComplete);
        var deletion = new ControlledAccountDeletionService
        {
            ReadAllResult = AccountDeletionTombstoneReadAllResult.Found([first, second]),
            ResumeHandler = (_, _) => Task.FromException<AccountDeletionResult>(
                new IOException("cleanup failed")),
        };
        var host = new AccountDeletionRecoveryHost(deletion);
        await host.ResumeConfirmedCleanupAsync();
        var snapshot = host.FailedCleanupRecords;

        host.MarkResolved(first);

        Assert.Equal([first, second], snapshot);
        Assert.Same(second, Assert.Single(host.FailedCleanupRecords));
        var mutable = Assert.IsAssignableFrom<IList<AccountDeletionTombstoneRecord>>(
            snapshot);
        Assert.Throws<NotSupportedException>(() => mutable.Add(first));
    }

    [Theory]
    [InlineData(AccountDeletionTombstoneReadStatus.Corrupt)]
    [InlineData(AccountDeletionTombstoneReadStatus.Unavailable)]
    public async Task UnsafeLedgerState_PerformsNoAutomaticCleanup(
        AccountDeletionTombstoneReadStatus status)
    {
        var deletion = new ControlledAccountDeletionService
        {
            ReadAllResult = status == AccountDeletionTombstoneReadStatus.Corrupt
                ? AccountDeletionTombstoneReadAllResult.Corrupt
                : AccountDeletionTombstoneReadAllResult.Unavailable,
        };

        await new AccountDeletionRecoveryHost(deletion)
            .ResumeConfirmedCleanupAsync();

        Assert.Empty(deletion.ResumeCalls);
        Assert.Empty(deletion.ClearCalls);
    }

    [Fact]
    public void StartupSource_AwaitsRecoveryBeforeCreatingWindow()
    {
        var app = Read("src", "App", "App.xaml.cs");
        var services = Read(
            "src",
            "App",
            "Services",
            "ServiceConfiguration.cs");
        var resume = app.IndexOf(
            "ResumeConfirmedCleanupAsync()",
            StringComparison.Ordinal);
        var window = app.IndexOf("new MainWindow()", StringComparison.Ordinal);

        Assert.Contains("protected override async void OnLaunched", app);
        Assert.True(resume >= 0);
        Assert.True(window > resume);
        Assert.Contains("catch", app[resume..window]);
        Assert.Contains("AddSingleton<AccountDeletionRecoveryHost>()", services);
        Assert.Contains("GetRequiredService<IAccountDeletionService>()", services);
        Assert.Contains("GetRequiredService<AccountDeletionRecoveryHost>()", services);
    }

    private static AccountDeletionCleanupFailure Failure(
        AccountDeletionCleanupTarget target) => new(
        target,
        AccountDeletionCleanupFailureKind.Unavailable);

    private static AccountDeletionTombstoneRecord Pending(
        string accountId,
        AccountDeletionStage stage)
    {
        var now = DateTimeOffset.UtcNow;
        return new AccountDeletionTombstoneRecord(
            Guid.NewGuid(),
            stage switch
            {
                AccountDeletionStage.Prepared => 1,
                AccountDeletionStage.ServerConfirmed => 2,
                AccountDeletionStage.LocalCleanupComplete => 3,
                AccountDeletionStage.UncertainLocalCleanupComplete => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(stage)),
            },
            now,
            stage == AccountDeletionStage.Prepared ? now : now.AddSeconds(1),
            new AccountDeletionBinding(
                accountId,
                $"{accountId}@example.com",
                ServerEndpoints.FromWebVault($"https://{accountId}.example"),
                Guid.NewGuid().ToString("D")),
            stage);
    }

    private static string Read(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException(parts[^1]);
    }
}
