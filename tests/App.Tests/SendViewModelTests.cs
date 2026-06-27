using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class SendViewModelTests
{
    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public int SecretCount { get; private set; }
        public int PlainCount { get; private set; }

        public void SetText(string text) { Text = text; PlainCount++; }
        public void SetSecretText(string text, int autoClearSeconds = 30) { Text = text; SecretCount++; }
    }

    [Fact]
    public void MockSendUiService_ReturnsThreeSends()
    {
        var service = new MockSendUiService();

        var sends = service.GetSends();

        Assert.Equal(3, sends.Count);
        Assert.Contains(sends, s => s.Type == SendType.Text);
        Assert.Contains(sends, s => s.Type == SendType.File);
    }

    [Fact]
    public void SelectFilterByTag_Text_ShowsOnlyTextSends()
    {
        var vm = new SendViewModel(new MockSendUiService());

        vm.SelectFilterByTag("send:text");

        Assert.NotEmpty(vm.FilteredItems);
        Assert.All(vm.FilteredItems, item => Assert.Equal(SendType.Text, item.Type));
    }

    [Fact]
    public void SelectFilterByTag_File_ShowsOnlyFileSends()
    {
        var vm = new SendViewModel(new MockSendUiService());

        vm.SelectFilterByTag("send:file");

        Assert.NotEmpty(vm.FilteredItems);
        Assert.All(vm.FilteredItems, item => Assert.Equal(SendType.File, item.Type));
    }

    [Fact]
    public void SelectFilterByTag_All_ShowsAllSends()
    {
        var vm = new SendViewModel(new MockSendUiService());

        vm.SelectFilterByTag("send:all");

        Assert.Equal(vm.Items.Count, vm.FilteredItems.Count);
    }

    [Fact]
    public void CopyLinkCommand_CopiesLinkWhenPresent()
    {
        var clipboard = new RecordingClipboard();
        var vm = new SendViewModel(new MockSendUiService(), clipboard);
        var item = vm.Items.First(s => !string.IsNullOrEmpty(s.Link));

        vm.CopyLinkCommand.Execute(item);

        Assert.Equal(item.Link, clipboard.Text);
    }

    [Fact]
    public void CreateSend_TextDraft_AddsTextSendToList()
    {
        var vm = new SendViewModel(new MockSendUiService());
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.Name = "一次性验证码";
        draft.Text = "123456";
        draft.DeletionDateLabel = "7 天";

        var created = vm.CreateSend(draft);

        Assert.True(created);
        Assert.Contains(vm.Items, s => s.Name == "一次性验证码" && s.Type == SendType.Text);
        Assert.Contains(vm.FilteredItems, s => s.Name == "一次性验证码");
    }

    [Fact]
    public void CreateSend_FileDraft_AddsFileSendToList()
    {
        var vm = new SendViewModel(new MockSendUiService());
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        draft.Name = "合同.zip";
        draft.FileName = "合同.zip";
        draft.DeletionDateLabel = "30 天";

        var created = vm.CreateSend(draft);

        Assert.True(created);
        Assert.Contains(vm.Items, s => s.Name == "合同.zip" && s.Type == SendType.File);
    }

    [Fact]
    public void CreateSend_MissingRequiredData_ReturnsFalse()
    {
        var vm = new SendViewModel(new MockSendUiService());
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.Name = "";
        draft.Text = "";

        var created = vm.CreateSend(draft);

        Assert.False(created);
        Assert.DoesNotContain(vm.Items, s => string.IsNullOrWhiteSpace(s.Name));
    }

    [Fact]
    public void MarkMoreMenuOpened_StoresSelectedSend()
    {
        var vm = new SendViewModel(new MockSendUiService());
        var item = vm.Items[0];

        vm.MarkMoreMenuOpened(item);

        Assert.Equal(item, vm.SelectedMenuItem);
    }

    [Fact]
    public void CopyLink_UsesSecretClipboard()
    {
        var clipboard = new RecordingClipboard();
        var vm = new SendViewModel(new MockSendUiService(), clipboard);
        var item = vm.FilteredItems[0];

        vm.CopyLinkCommand.Execute(item);

        Assert.Equal(item.Link, clipboard.Text);
        Assert.Equal(1, clipboard.SecretCount);
        Assert.Equal(0, clipboard.PlainCount);
    }

    [Fact]
    public void MockSendUiService_DeleteSend_RemovesAndReportsResult()
    {
        var service = new MockSendUiService();
        var first = service.GetSends()[0];

        Assert.True(service.DeleteSend(first.Id));
        Assert.DoesNotContain(service.GetSends(), s => s.Id == first.Id);
        Assert.False(service.DeleteSend("does-not-exist"));
    }

    [Fact]
    public void MockSendUiService_UpdateSend_ReplacesNameAndType()
    {
        var service = new MockSendUiService();
        var target = service.GetSends().First(s => s.Type == SendType.Text);
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.Name = "更新后的名称";
        draft.Text = "新内容";
        draft.DeletionDateLabel = "30 天";

        var updated = service.UpdateSend(target.Id, draft);

        Assert.NotNull(updated);
        Assert.Equal("更新后的名称", updated!.Name);
        Assert.Equal(target.Id, updated.Id);
        Assert.Contains(service.GetSends(), s => s.Id == target.Id && s.Name == "更新后的名称");
        Assert.Null(service.UpdateSend("does-not-exist", draft));
    }

    [Fact]
    public void DeleteSendCommand_RemovesFromItemsAndFiltered()
    {
        var vm = new SendViewModel(new MockSendUiService());
        var item = vm.Items[0];
        var startCount = vm.Items.Count;

        vm.DeleteSendCommand.Execute(item);

        Assert.Equal(startCount - 1, vm.Items.Count);
        Assert.DoesNotContain(vm.Items, s => s.Id == item.Id);
        Assert.DoesNotContain(vm.FilteredItems, s => s.Id == item.Id);
    }

    [Fact]
    public void DeleteSendCommand_LastItem_UpdatesHasItemsNoItems()
    {
        var vm = new SendViewModel(new MockSendUiService());
        foreach (var item in vm.Items.ToList())
            vm.DeleteSendCommand.Execute(item);

        Assert.False(vm.HasItems);
        Assert.True(vm.NoItems);
    }

    [Fact]
    public void DeleteSendCommand_NullItem_NoOp()
    {
        var vm = new SendViewModel(new MockSendUiService());
        var startCount = vm.Items.Count;

        vm.DeleteSendCommand.Execute(null);

        Assert.Equal(startCount, vm.Items.Count);
    }

    [Fact]
    public void UpdateSendFromDraft_ReplacesItemInCollections()
    {
        var vm = new SendViewModel(new MockSendUiService());
        var item = vm.Items.First(s => s.Type == SendType.Text);
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.Name = "改名后的 Send";
        draft.Text = "新文本";
        draft.DeletionDateLabel = "1 天";

        var ok = vm.UpdateSendFromDraft(item, draft);

        Assert.True(ok);
        Assert.Contains(vm.Items, s => s.Id == item.Id && s.Name == "改名后的 Send");
        Assert.Contains(vm.FilteredItems, s => s.Id == item.Id && s.Name == "改名后的 Send");
        Assert.DoesNotContain(vm.Items, s => ReferenceEquals(s, item));
    }

    [Fact]
    public void UpdateSendFromDraft_InvalidDraft_ReturnsFalse()
    {
        var vm = new SendViewModel(new MockSendUiService());
        var item = vm.Items.First(s => s.Type == SendType.Text);
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.Name = "";
        var startCount = vm.Items.Count;

        Assert.False(vm.UpdateSendFromDraft(item, draft));
        Assert.Equal(startCount, vm.Items.Count);
    }

    [Fact]
    public void UpdateSendFromDraft_UnknownItem_ReturnsFalse()
    {
        var vm = new SendViewModel(new MockSendUiService());
        var bogus = new SendListItem("does-not-exist", "x", SendType.Text, "", null);
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.Name = "有效名称";
        draft.Text = "有效文本";
        draft.DeletionDateLabel = "7 天";
        var startCount = vm.Items.Count;

        Assert.False(vm.UpdateSendFromDraft(bogus, draft));
        Assert.Equal(startCount, vm.Items.Count);
    }

    [Fact]
    public void SendEditorDraft_FromExisting_CopiesNameAndType()
    {
        var item = new SendListItem("s9", "周报.pdf", SendType.File, "7 天 后删除", "https://x/s9");

        var draft = SendEditorDraft.FromExisting(item);

        Assert.Equal("周报.pdf", draft.Name);
        Assert.Equal(SendType.File, draft.Type);
        Assert.Equal("周报.pdf", draft.FileName);
    }
}
