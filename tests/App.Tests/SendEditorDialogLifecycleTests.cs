using App.ViewModels.Models;
using App.Views;
using Xunit;

namespace App.Tests;

public class SendEditorDialogLifecycleTests
{
    [Fact]
    public void SetType_WhenBindingsAreNotReady_DoesNotThrowAndUpdatesDraft()
    {
        var draft = SendEditorDraft.CreateDefault(SendType.Text);

        var exception = Record.Exception(() =>
            SendEditorDialogLifecycle.SetType(draft, SendType.File, canUpdateBindings: false, updateBindings: null));

        Assert.Null(exception);
        Assert.Equal(SendType.File, draft.Type);
    }

    [Fact]
    public void SetType_WhenInitializationIsNotComplete_DoesNotInvokeExistingUpdater()
    {
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        var updateCount = 0;

        SendEditorDialogLifecycle.SetType(
            draft,
            SendType.Text,
            canUpdateBindings: false,
            updateBindings: () => updateCount++);

        Assert.Equal(SendType.Text, draft.Type);
        Assert.Equal(0, updateCount);
    }

    [Fact]
    public void SetType_WhenBindingsAreReady_RefreshesBindings()
    {
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        var updateCount = 0;

        SendEditorDialogLifecycle.SetType(draft, SendType.Text, canUpdateBindings: true, () => updateCount++);

        Assert.Equal(SendType.Text, draft.Type);
        Assert.Equal(1, updateCount);
    }
}
