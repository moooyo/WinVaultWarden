using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Core.Models;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace App.Tests;

public sealed class SendSessionOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadStartedInAccountA_CannotCommitAfterSwitchToAccountB(bool emptySnapshot)
    {
        var session = NewSession(out _);
        var lifecycle = new ImmediateLifecycle();
        var service = new ControlledUiService { DeferLoad = true };
        var viewModel = new SendViewModel(service, null, session, lifecycle);

        var load = viewModel.LoadAsync(CancellationToken.None);
        await service.LoadStarted.Task.WaitAsync(CancellationToken.None);
        SwitchSession(session);
        service.CompleteLoad(emptySnapshot
            ? Array.Empty<SendListItem>()
            : [new SendListItem("account-a", "A secret", SendType.Text, "later", null)]);

        var result = await load;

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Empty(viewModel.Items);
        Assert.Null(viewModel.LoadedSession);
        if (emptySnapshot)
            Assert.NotEqual(DataPageState.Empty, viewModel.LoadState);
    }

    [Fact]
    public async Task LoadAndDraft_CurrentSession_NormalPathCarriesOriginatingLease()
    {
        var session = NewSession(out var lease);
        var lifecycle = new ImmediateLifecycle();
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.Name = "draft";
        draft.Text = "secret";
        var service = new ControlledUiService
        {
            LoadResult = [new SendListItem("send-1", "name", SendType.Text, "later", null)],
            DraftResult = draft,
        };
        var viewModel = new SendViewModel(service, null, session, lifecycle);

        var load = await viewModel.LoadAsync(CancellationToken.None);
        var loadedDraft = await viewModel.GetEditDraftAsync("send-1", CancellationToken.None);

        Assert.True(load.IsSuccess);
        Assert.True(lease.SameSessionAs(viewModel.LoadedSession));
        Assert.Same(draft, loadedDraft);
        Assert.True(lease.SameSessionAs(loadedDraft!.OriginatingSession));
    }

    [Fact]
    public async Task DraftReadInAccountA_IsClearedAndRejectedAfterSwitchToAccountB()
    {
        var session = NewSession(out _);
        var lifecycle = new ImmediateLifecycle();
        var service = new ControlledUiService { DeferDraft = true };
        var viewModel = new SendViewModel(service, null, session, lifecycle);
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.Name = "A draft";
        draft.Text = "A secret";

        var read = viewModel.GetEditDraftAsync("send-1", CancellationToken.None);
        await service.DraftStarted.Task.WaitAsync(CancellationToken.None);
        SwitchSession(session);
        service.CompleteDraft(draft);

        Assert.Null(await read);
        Assert.Equal(string.Empty, draft.Text);
        Assert.Null(draft.OriginatingSession);
    }

    [Fact]
    public async Task AccountADraft_CannotStartCreateAfterSwitchToAccountB()
    {
        var session = NewSession(out var accountA);
        var lifecycle = new ImmediateLifecycle();
        var service = new ControlledUiService();
        var viewModel = new SendViewModel(service, null, session, lifecycle);
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.Name = "A draft";
        draft.Text = "A secret";
        draft.OriginatingSession = accountA;
        SwitchSession(session);

        var result = await viewModel.CreateSendAsync(draft, null, CancellationToken.None);

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Equal(0, service.CreateCalls);
        Assert.Null(viewModel.Error);
    }

    [Fact]
    public async Task AccountADeleteCompletion_CannotRemoveAccountBItemWithSameId()
    {
        var session = NewSession(out var accountA);
        var lifecycle = new ImmediateLifecycle();
        var accountAItem = new SendListItem("same-id", "Account A", SendType.Text, "later", null);
        var service = new ControlledUiService
        {
            LoadResult = [accountAItem],
            DeferDelete = true,
        };
        var viewModel = new SendViewModel(service, null, session, lifecycle);
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.DeleteSendCommand.Execute(new SendDeleteRequest(accountAItem, accountA));
        await service.DeleteStarted.Task.WaitAsync(CancellationToken.None);
        SwitchSession(session);
        viewModel.Items.Clear();
        viewModel.Items.Add(new SendListItem("same-id", "Account B", SendType.Text, "later", null));
        service.CompleteDelete();
        await viewModel.DeleteSendCommand.ExecutionTask!;

        Assert.Equal("Account B", Assert.Single(viewModel.Items).Name);
        Assert.True(accountA.SameSessionAs(service.DeleteSession));
        Assert.Null(viewModel.Error);
    }

    [Fact]
    public void SessionBoundStart_RejectsAccountADraftAfterSwitchToAccountB()
    {
        var session = NewSession(out var accountA);
        var lifecycle = new ImmediateLifecycle();
        SwitchSession(session);
        var registered = false;
        var started = false;

        var operation = SessionBoundOperationStarter.TryStart(
            lifecycle,
            session,
            accountA,
            () =>
            {
                registered = true;
                return new CallbackDisposable(() => { });
            },
            () =>
            {
                started = true;
                return new object();
            });

        Assert.Null(operation);
        Assert.False(registered);
        Assert.False(started);
    }

    [Fact]
    public void SessionBoundStart_CurrentSession_RegistersBeforeStarting()
    {
        var session = NewSession(out var lease);
        var events = new List<string>();
        var lifecycle = new ImmediateLifecycle
        {
            OnSensitive = () => events.Add("lifecycle"),
        };

        using var operation = SessionBoundOperationStarter.TryStart(
            lifecycle,
            session,
            lease,
            () =>
            {
                events.Add("register");
                return new CallbackDisposable(() => events.Add("dispose"));
            },
            () =>
            {
                events.Add("start");
                return new object();
            });

        Assert.NotNull(operation);
        Assert.Equal(["lifecycle", "register", "start"], events);
    }

    [Fact]
    public void SendPageUsesOriginatingSessionForEditorAndDeleteDialogStarts()
    {
        var page = ReadRepoFile("src", "App", "Views", "SendPage.xaml.cs");
        var helper = ReadRepoFile("src", "App", "Views", "DialogHelper.cs");
        var boundStart = helper.IndexOf("SessionBoundOperationStarter.TryStart", StringComparison.Ordinal);
        var showStart = helper.IndexOf("return dialog.ShowAsync();", boundStart, StringComparison.Ordinal);

        Assert.Contains("draft.OriginatingSession", page, StringComparison.Ordinal);
        Assert.Contains("ViewModel.LoadedSession", page, StringComparison.Ordinal);
        Assert.Contains("new SendDeleteRequest(item, expectedSession)", page, StringComparison.Ordinal);
        Assert.True(boundStart >= 0 && showStart > boundStart);
    }

    private static VaultSession NewSession(out VaultSessionLease lease)
    {
        var session = new VaultSession();
        lease = session.BeginAccountSession(
            "account-a",
            "a@example.test",
            ServerEndpoints.ForSelfHosted("https://a.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        return session;
    }

    private static void SwitchSession(VaultSession session)
    {
        session.BeginAccountSession(
            "account-b",
            "b@example.test",
            ServerEndpoints.ForSelfHosted("https://b.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
    }

    private static string ReadRepoFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }

    private sealed class ImmediateLifecycle : IAppLifecycleCoordinator
    {
        public Action? OnSensitive { get; init; }
        public bool IsTransitioning => false;
        public Task RequestAsync(AppLifecycleIntent intent, VaultSessionLease? expectedSession = null) =>
            Task.CompletedTask;
        public async Task<bool> RequestIfAsync(
            AppLifecycleIntent intent,
            Func<bool> predicate)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            bool accepted;
            try
            {
                accepted = predicate();
            }
            catch
            {
                return false;
            }

            if (!accepted)
                return false;

            await RequestAsync(intent);
            return true;
        }
        public bool MarkSessionReady() => true;
        public bool TryRunSensitive(Func<bool> action)
        {
            OnSensitive?.Invoke();
            return action();
        }
        public void FailClosedSynchronously() { }
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    private sealed class ControlledUiService : ISendUiService
    {
        private readonly TaskCompletionSource<IReadOnlyList<SendListItem>> _load =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<SendEditorDraft?> _draft =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _delete =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool DeferLoad { get; init; }
        public bool DeferDraft { get; init; }
        public bool DeferDelete { get; init; }
        public IReadOnlyList<SendListItem> LoadResult { get; init; } = [];
        public SendEditorDraft? DraftResult { get; init; }
        public int CreateCalls { get; private set; }
        public VaultSessionLease? DeleteSession { get; private set; }
        public TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DraftStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeleteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default)
        {
            LoadStarted.TrySetResult();
            return DeferLoad ? _load.Task : Task.FromResult(LoadResult);
        }

        public Task<SendEditorDraft?> GetEditDraftAsync(string id, CancellationToken ct = default)
        {
            DraftStarted.TrySetResult();
            return DeferDraft ? _draft.Task : Task.FromResult(DraftResult);
        }

        public Task<SendListItem> CreateSendAsync(
            SendEditorDraft draft,
            SendFileSource? file,
            CancellationToken ct = default)
        {
            CreateCalls++;
            return Task.FromResult(new SendListItem("created", draft.Name, draft.Type, "later", null));
        }

        public Task<SendListItem> UpdateSendAsync(
            string id,
            SendEditorDraft draft,
            SendFileSource? file,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteSendAsync(
            string id,
            VaultSessionLease originatingSession,
            CancellationToken ct = default)
        {
            DeleteSession = originatingSession;
            DeleteStarted.TrySetResult();
            return DeferDelete ? _delete.Task : Task.CompletedTask;
        }

        public string? CopyShareLink(SendListItem item) => item.Link;

        public Task<SendReceivedResult> OpenReceivedLinkAsync(
            string url,
            string? password,
            CancellationToken ct = default) => throw new NotSupportedException();

        public void CompleteLoad(IReadOnlyList<SendListItem> items) => _load.TrySetResult(items);
        public void CompleteDraft(SendEditorDraft? draft) => _draft.TrySetResult(draft);
        public void CompleteDelete() => _delete.TrySetResult();
    }
}
