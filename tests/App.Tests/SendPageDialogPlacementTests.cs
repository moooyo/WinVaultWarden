using Xunit;

namespace App.Tests;

public class SendPageDialogPlacementTests
{
    [Fact]
    public void SendPage_UsesInlineEditorWithLifecycleCleanupAndUnsavedGuard()
    {
        var source = LoadSendPageCodeBehind();

        Assert.DoesNotContain("new SendEditorDialog(", source);
        Assert.Contains("Draft = draft", source);
        Assert.Contains("Bindings.Update()", source);
        Assert.Contains("CanNavigateAwayAsync", source);
        Assert.Contains("Register(CancelPageLifetime, ClearPageSensitive, ClearPageSensitive)", source);
        Assert.Contains("_draft.ClearSensitive()", source);
    }

    [Fact]
    public void SendPage_StaleEditReadCannotReplaceANewerDraft()
    {
        var source = LoadSendPageCodeBehind();
        var method = ReadMethod(source, "private async Task EditItemAsync", "private async Task<SendEditorDraft?> LoadDraftAsync");
        var begin = method.IndexOf("TryBeginPageRequest(out var request)", StringComparison.Ordinal);
        var read = method.IndexOf("await LoadDraftAsync(item.Id, request)", StringComparison.Ordinal);
        var origin = method.IndexOf("request.Session.SameSessionAs(draft.OriginatingSession)", StringComparison.Ordinal);
        var commit = method.IndexOf("TryCommitPageRequest(request", StringComparison.Ordinal);
        var close = method.IndexOf("CloseEditor()", StringComparison.Ordinal);
        var open = method.IndexOf("OpenEditor(item, draft)", StringComparison.Ordinal);

        Assert.True(begin >= 0 && read > begin && origin > read && commit > origin && close > commit && open > close);
        Assert.Contains("draft.ClearSensitive()", method);
    }

    [Fact]
    public void SendPage_ReadCommitsRequireCurrentGenerationLifetimeAndOriginalUnlockedSession()
    {
        var source = LoadSendPageCodeBehind();
        var guard = ReadMethod(source, "private bool IsCurrentPageRequest", "public SendPage()");

        Assert.Contains("request.Generation == Volatile.Read(ref _selectionRequest)", guard);
        Assert.Contains("ReferenceEquals(request.Lifetime, _pageLifetimeCts)", guard);
        Assert.Contains("!request.Token.IsCancellationRequested", guard);
        Assert.Contains("lifecycle.TryRunSensitive", guard);
        Assert.Contains("session.TryRunIfUnlocked(request.Session", guard);
        Assert.Contains("if (!IsCurrentPageRequest(request)) return", guard);
    }

    [Theory]
    [InlineData("public async void BeginCreate", "public void RefreshSendList")]
    [InlineData("public async void SelectFilter", "public void ApplySearch")]
    [InlineData("private async void OnSendSelectionChanged", "private async Task ShowDetailAsync")]
    [InlineData("private async void OnBackToListClick", "private void OnDownloadOwnFileClick")]
    public void SendPage_NewUserIntentInvalidatesPendingReadsBeforeWaiting(string start, string end)
    {
        var method = ReadMethod(LoadSendPageCodeBehind(), start, end);
        var begin = method.IndexOf("TryBeginPageRequest(out var request)", StringComparison.Ordinal);
        var wait = method.IndexOf("await CanNavigateAwayAsync()", StringComparison.Ordinal);
        Assert.True(begin >= 0 && wait > begin);
    }

    [Fact]
    public void SendPage_ConfirmationCannotDiscardADraftChangedWhileWaiting()
    {
        var method = ReadMethod(LoadSendPageCodeBehind(), "public async Task<bool> CanNavigateAwayAsync", "public async void SelectFilter");
        Assert.Contains("!ReferenceEquals(draft, _draft)", method);
        Assert.Contains("generation != Volatile.Read(ref _selectionRequest)", method);
        Assert.Contains("snapshot != CaptureEditorSnapshot()", method);
    }

    [Fact]
    public void SendPage_DisposedPageLifetimeRejectsTheReadWithoutThrowing()
    {
        var source = LoadSendPageCodeBehind();
        var capture = ReadMethod(source, "private bool TryCapturePageLifetime", "private bool IsCurrentPageRequest");
        Assert.Contains("token = lifetime.Token", capture);
        Assert.Contains("catch (ObjectDisposedException)", capture);
        var initialLoad = ReadMethod(source, "private async Task LoadInitialAsync", "private static SendListItem? ItemFromSender");
        Assert.Contains("TryCapturePageLifetime(out var lifetime, out var token)", initialLoad);
        Assert.DoesNotContain("lifetime.Token", initialLoad);
    }

    private static string ReadMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static string LoadSendPageCodeBehind()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "SendPage.xaml.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find SendPage.xaml.cs from the test output directory.");
    }
}
