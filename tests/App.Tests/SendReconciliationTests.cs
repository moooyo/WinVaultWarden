using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Xunit;
using AppSendType = App.ViewModels.Models.SendType;
using CoreSendType = Core.Enums.SendType;

namespace App.Tests;

public sealed class SendReconciliationTests
{
    [Fact]
    public async Task ViewModelLoad_FirstSupersededThenFreshSnapshot_CommitsFreshItems()
    {
        var fresh = new SendListItem("fresh", "fresh", AppSendType.Text, "later", null);
        var service = new SequencedUiService(
            () => Task.FromException<IReadOnlyList<SendListItem>>(new SendSnapshotSupersededException()),
            () => Task.FromResult<IReadOnlyList<SendListItem>>([fresh]));
        var viewModel = new SendViewModel(service);

        var result = await viewModel.LoadAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("fresh", Assert.Single(viewModel.Items).Id);
        Assert.Equal(DataPageState.Content, viewModel.LoadState);
    }

    [Fact]
    public async Task ViewModelLoad_TwoSupersededSnapshots_PreservesStableState()
    {
        var stable = new SendListItem("stable", "stable", AppSendType.Text, "later", null);
        var service = new SequencedUiService(
            () => Task.FromResult<IReadOnlyList<SendListItem>>([stable]),
            () => Task.FromException<IReadOnlyList<SendListItem>>(new SendSnapshotSupersededException()),
            () => Task.FromException<IReadOnlyList<SendListItem>>(new SendSnapshotSupersededException()));
        var viewModel = new SendViewModel(service);
        await viewModel.LoadAsync(CancellationToken.None);

        var result = await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Equal("stable", Assert.Single(viewModel.Items).Id);
        Assert.Equal(DataPageState.Content, viewModel.LoadState);
        Assert.Null(viewModel.Error);
    }

    [Fact]
    public async Task Update_StaleRevisionAndDifferentWitness_RemainsPendingUntilWitnessMatches()
    {
        var receiptRevision = DateTimeOffset.Parse("2026-08-29T10:00:01Z");
        var read = new SnapshotSendService
        {
            Snapshot = Snapshot(Observation("send-1", "old", receiptRevision.AddSeconds(-1), "old-witness")),
        };
        var write = new ControlledWriteService
        {
            UpdateReceipt = new SendMutationReceipt(
                "send-1",
                receiptRevision,
                SendMutationKind.Update,
                RawMutationWitness: "committed-witness"),
        };
        var (service, lease) = Service(read, write);

        var committed = await service.UpdateSendAsync(
            "send-1",
            TextDraft(lease),
            file: null,
            CancellationToken.None);

        Assert.Equal(SendReconciliationState.CommittedPendingRefresh, committed.ReconciliationState);
        Assert.False(committed.CanModify);

        read.Snapshot = Snapshot(Observation(
            "send-1",
            "new",
            receiptRevision.AddSeconds(-1),
            "committed-witness"));
        var reconciled = Assert.Single(await service.GetSendsAsync(CancellationToken.None));

        Assert.Equal("new", reconciled.Name);
        Assert.Equal(SendReconciliationState.None, reconciled.ReconciliationState);
        Assert.True(reconciled.CanModify);
    }

    [Fact]
    public async Task ReadStartedBeforeMutation_CannotCommitAfterReceiptWasReconciled()
    {
        var read = new DelayedFirstSnapshotSendService
        {
            Latest = Snapshot(Observation("send-1", "new", null, "committed-witness")),
        };
        var write = new ControlledWriteService
        {
            UpdateReceipt = new SendMutationReceipt(
                "send-1",
                null,
                SendMutationKind.Update,
                RawMutationWitness: "committed-witness"),
        };
        var (service, lease) = Service(read, write);

        var oldLoad = service.GetSendsAsync(CancellationToken.None);
        await read.FirstReadStarted.Task.WaitAsync(CancellationToken.None);

        var committed = await service.UpdateSendAsync(
            "send-1",
            TextDraft(lease),
            file: null,
            CancellationToken.None);
        Assert.Equal(SendReconciliationState.None, committed.ReconciliationState);

        read.CompleteFirst(Snapshot(Observation("send-1", "old", null, "old-witness")));
        await Assert.ThrowsAsync<SendSnapshotSupersededException>(() => oldLoad);
    }

    [Fact]
    public async Task EditReadStartedBeforeMutation_CannotReturnStaleDraftAfterReconciliation()
    {
        var read = new DelayedFirstSnapshotSendService
        {
            Latest = Snapshot(Observation("send-1", "new", null, "committed-witness")),
        };
        var write = new ControlledWriteService
        {
            UpdateReceipt = new SendMutationReceipt(
                "send-1",
                null,
                SendMutationKind.Update,
                RawMutationWitness: "committed-witness"),
        };
        var (service, lease) = Service(read, write);

        var staleEdit = service.GetEditDraftAsync("send-1", CancellationToken.None);
        await read.FirstReadStarted.Task.WaitAsync(CancellationToken.None);
        await service.UpdateSendAsync(
            "send-1",
            TextDraft(lease),
            file: null,
            CancellationToken.None);

        read.CompleteFirst(Snapshot(Observation("send-1", "old", null, "old-witness")));
        await Assert.ThrowsAsync<SendSnapshotSupersededException>(() => staleEdit);
    }

    [Fact]
    public async Task AccountASnapshot_CannotApplyReconciliationAfterSwitchToAccountB()
    {
        var session = new Vault.VaultSession();
        var accountA = session.BeginAccountSession(
            "account-a",
            "a@example.test",
            ServerEndpoints.ForSelfHosted("https://a.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        var read = new DelayedFirstSnapshotSendService { SessionLease = accountA };
        var service = new SendUiService(
            read,
            new ControlledWriteService(),
            new ThrowingAccessService(),
            session);

        var load = service.GetSendsAsync(CancellationToken.None);
        await read.FirstReadStarted.Task.WaitAsync(CancellationToken.None);
        session.BeginAccountSession(
            "account-b",
            "b@example.test",
            ServerEndpoints.ForSelfHosted("https://b.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        read.CompleteFirst(new SendReadSnapshot(
            [Observation("send-a", "Account A", null, "witness-a")],
            accountA,
            IsComplete: true));

        await Assert.ThrowsAsync<SendSnapshotSupersededException>(() => load);
    }

    [Fact]
    public async Task UncertainReplacement_CorruptOldRawObservation_KeepsBothIdsLocked()
    {
        var replacement = new SendMutationReceipt(
            "new-send",
            null,
            SendMutationKind.Replace,
            ReplacedSendId: "old-send");
        var read = new SnapshotSendService
        {
            Snapshot = Snapshot(
                new SendObservation("old-send", null, SendDecryptStatus.Corrupt, null, "old-raw"),
                Observation("new-send", "new", null, "new-raw")),
        };
        var write = new ControlledWriteService
        {
            ReplaceError = new Vault.SendReplacementUncertainException(
                "old-send",
                replacement,
                new HttpRequestException("response lost")),
        };
        var (service, lease) = Service(read, write);

        var result = await service.UpdateSendAsync(
            "old-send",
            FileDraft(lease),
            MemorySource(),
            CancellationToken.None);
        var items = await service.GetSendsAsync(CancellationToken.None);

        Assert.Equal(SendReconciliationState.ReplacementNewPending, result.ReconciliationState);
        Assert.Equal(
            SendReconciliationState.ReplacementOriginalPending,
            items.Single(item => item.Id == "old-send").ReconciliationState);
        Assert.Equal(
            SendReconciliationState.ReplacementNewPending,
            items.Single(item => item.Id == "new-send").ReconciliationState);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteSendAsync("old-send", lease, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteSendAsync("new-send", lease, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentReplacement_OnlyOneOperationCanReserveExistingId()
    {
        var read = new SnapshotSendService
        {
            Snapshot = Snapshot(Observation("new-send", "new", null, "new-raw")),
        };
        var write = new ControlledWriteService
        {
            BlockReplacement = true,
        };
        var (service, lease) = Service(read, write);
        var draft = FileDraft(lease);
        var source = MemorySource();

        var first = service.UpdateSendAsync(
            "old-send",
            draft,
            source,
            CancellationToken.None);
        await write.ReplacementStarted.Task.WaitAsync(CancellationToken.None);

        await Assert.ThrowsAsync<SendMutationInProgressException>(() =>
            service.UpdateSendAsync(
                "old-send",
                draft,
                source,
                CancellationToken.None));
        Assert.Equal(1, write.ReplaceCalls);

        write.CompleteReplacement(new SendMutationReceipt(
            "new-send",
            null,
            SendMutationKind.Replace,
            SessionLease: lease,
            ReplacedSendId: "old-send"));
        var committed = await first;

        Assert.Equal("new-send", committed.Id);
        Assert.Equal(SendReconciliationState.None, committed.ReconciliationState);
        Assert.Equal(1, write.ReplaceCalls);
    }

    private static (SendUiService Service, VaultSessionLease Lease) Service(
        ISendService read,
        ISendWriteService write)
    {
        var session = new Vault.VaultSession();
        var lease = session.BeginAccountSession(
            "test-account",
            "test@example.test",
            ServerEndpoints.ForSelfHosted("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        if (read is SnapshotSendService snapshot)
            snapshot.SessionLease = lease;
        if (read is DelayedFirstSnapshotSendService delayed)
            delayed.SessionLease = lease;
        return (new SendUiService(read, write, new ThrowingAccessService(), session), lease);
    }

    private static SendEditorDraft TextDraft(VaultSessionLease lease)
    {
        var draft = SendEditorDraft.CreateDefault(AppSendType.Text);
        draft.OriginatingSession = lease;
        draft.Name = "name";
        draft.Text = "content";
        return draft;
    }

    private static SendEditorDraft FileDraft(VaultSessionLease lease)
    {
        var draft = SendEditorDraft.CreateDefault(AppSendType.File);
        draft.OriginatingSession = lease;
        draft.Name = "file";
        draft.FileName = "file.bin";
        return draft;
    }

    private static SendFileSource MemorySource() => new(
        "file.bin",
        1,
        _ => ValueTask.FromResult<Stream>(new MemoryStream([1], writable: false)));

    private static SendObservation Observation(
        string id,
        string name,
        DateTimeOffset? revision,
        string witness) =>
        new(
            id,
            revision,
            SendDecryptStatus.Decrypted,
            new Send
            {
                Id = id,
                AccessId = "access-" + id,
                Type = CoreSendType.Text,
                Name = name,
                DeletionDate = DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
                RevisionDate = revision,
                RawMutationWitness = witness,
            },
            witness);

    private static SendReadSnapshot Snapshot(params SendObservation[] observations) =>
        new(observations, IsComplete: true);

    private sealed class SnapshotSendService : ISendService
    {
        public SendReadSnapshot Snapshot { get; set; } = new(Array.Empty<SendObservation>());
        public VaultSessionLease? SessionLease { get; set; }

        public Task<IReadOnlyList<Send>> GetSendsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Send>>(Snapshot.Observations
                .Where(item => item.DecryptStatus == SendDecryptStatus.Decrypted)
                .Select(item => item.Value!)
                .ToArray());

        public Task<SendReadSnapshot> ObserveSendsAsync(CancellationToken ct = default) =>
            Task.FromResult(Snapshot with { SessionLease = SessionLease });
    }

    private sealed class DelayedFirstSnapshotSendService : ISendService
    {
        private readonly TaskCompletionSource<SendReadSnapshot> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource FirstReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SendReadSnapshot Latest { get; set; } = new(Array.Empty<SendObservation>());
        public VaultSessionLease? SessionLease { get; set; }

        public Task<IReadOnlyList<Send>> GetSendsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Send>>(Array.Empty<Send>());

        public Task<SendReadSnapshot> ObserveSendsAsync(CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) != 1)
                return Task.FromResult(Latest with { SessionLease = SessionLease });
            FirstReadStarted.TrySetResult();
            return AwaitFirstAsync();
        }

        private async Task<SendReadSnapshot> AwaitFirstAsync() =>
            (await _first.Task) with { SessionLease = SessionLease };

        public void CompleteFirst(SendReadSnapshot snapshot) => _first.TrySetResult(snapshot);
    }

    private sealed class ControlledWriteService : ISendWriteService
    {
        private readonly TaskCompletionSource<SendMutationReceipt> _replacement =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SendMutationReceipt UpdateReceipt { get; set; } =
            new("send-1", null, SendMutationKind.Update);
        public Exception? ReplaceError { get; set; }
        public bool BlockReplacement { get; set; }
        public int ReplaceCalls { get; private set; }
        public TaskCompletionSource ReplacementStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SendMutationReceipt> SaveTextSendAsync(
            SendDraftModel draft,
            VaultSessionLease expectedSession,
            CancellationToken ct = default) =>
            Task.FromResult(UpdateReceipt with { SessionLease = expectedSession });

        public Task<SendMutationReceipt> CreateFileSendAsync(
            SendDraftModel draft,
            SendFileSource file,
            VaultSessionLease expectedSession,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<SendMutationReceipt> UpdateFileSendMetadataAsync(
            SendDraftModel draft,
            VaultSessionLease expectedSession,
            CancellationToken ct = default) =>
            Task.FromResult(UpdateReceipt with { SessionLease = expectedSession });

        public Task<SendMutationReceipt> ReplaceFileSendAsync(
            string existingSendId,
            SendDraftModel draft,
            SendFileSource file,
            VaultSessionLease expectedSession,
            CancellationToken ct = default)
        {
            ReplaceCalls++;
            ReplacementStarted.TrySetResult();
            if (ReplaceError is not null)
            {
                if (ReplaceError is Vault.SendReplacementUncertainException uncertain)
                {
                    return Task.FromException<SendMutationReceipt>(
                        new Vault.SendReplacementUncertainException(
                            uncertain.ExistingSendId,
                            uncertain.Replacement with { SessionLease = expectedSession },
                            uncertain.InnerException ?? uncertain));
                }
                return Task.FromException<SendMutationReceipt>(ReplaceError);
            }
            return BlockReplacement
                ? _replacement.Task
                : Task.FromResult(new SendMutationReceipt(
                    "new-send",
                    null,
                    SendMutationKind.Replace,
                    SessionLease: expectedSession,
                    ReplacedSendId: existingSendId));
        }

        public Task<SendMutationReceipt> DeleteSendAsync(
            string sendId,
            VaultSessionLease expectedSession,
            CancellationToken ct = default) =>
            Task.FromResult(new SendMutationReceipt(
                sendId,
                null,
                SendMutationKind.Delete,
                SessionLease: expectedSession));

        public void CompleteReplacement(SendMutationReceipt receipt) =>
            _replacement.TrySetResult(receipt);
    }

    private sealed class ThrowingAccessService : ISendAccessService
    {
        public Task<SendAccessResult> AccessAsync(
            string shareUrl,
            string? password,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<long> DownloadFileAsync(
            SendDownloadLease download,
            Stream plaintextDestination,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class SequencedUiService : ISendUiService
    {
        private readonly Queue<Func<Task<IReadOnlyList<SendListItem>>>> _loads;

        public SequencedUiService(params Func<Task<IReadOnlyList<SendListItem>>>[] loads) =>
            _loads = new Queue<Func<Task<IReadOnlyList<SendListItem>>>>(loads);

        public Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default) =>
            _loads.Dequeue()();

        public Task<SendEditorDraft?> GetEditDraftAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SendListItem> CreateSendAsync(
            SendEditorDraft draft,
            SendFileSource? file,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<SendListItem> UpdateSendAsync(
            string id,
            SendEditorDraft draft,
            SendFileSource? file,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteSendAsync(
            string id,
            VaultSessionLease originatingSession,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public string? CopyShareLink(SendListItem item) => throw new NotSupportedException();

        public Task<SendReceivedResult> OpenReceivedLinkAsync(
            string url,
            string? password,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
