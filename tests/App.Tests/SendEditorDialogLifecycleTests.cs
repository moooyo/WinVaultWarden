using App.ViewModels.Models;
using App.Views;
using Xunit;

namespace App.Tests;

public class SendEditorDialogLifecycleTests
{
    [Fact]
    public void InitializationCheckedEvent_DoesNotChangeExistingFileType()
    {
        // 编辑既有文件 Send 时,对话框初始化会先触发默认 Text RadioButton 的 Checked,此时 bindings 尚未就绪
        // (canUpdateBindings=false)。必须忽略该早期事件,否则会把 File 草稿类型误改成 Text 并清空文件选择。
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        var updates = 0;

        SendEditorDialogLifecycle.SetType(
            draft,
            SendType.Text,
            canUpdateBindings: false,
            () => updates++);

        Assert.Equal(SendType.File, draft.Type);
        Assert.Equal(0, updates);
    }

    [Fact]
    public void SetType_WhenBindingsAreReady_ChangesTypeAndRefreshesBindings()
    {
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        var updateCount = 0;

        SendEditorDialogLifecycle.SetType(draft, SendType.Text, canUpdateBindings: true, () => updateCount++);

        Assert.Equal(SendType.Text, draft.Type);
        Assert.Equal(1, updateCount);
    }

    [Fact]
    public void SetType_WhenBindingsAreReady_ToleratesNullUpdater()
    {
        var draft = SendEditorDraft.CreateDefault(SendType.Text);

        var exception = Record.Exception(() =>
            SendEditorDialogLifecycle.SetType(draft, SendType.File, canUpdateBindings: true, updateBindings: null));

        Assert.Null(exception);
        Assert.Equal(SendType.File, draft.Type);
    }
}
