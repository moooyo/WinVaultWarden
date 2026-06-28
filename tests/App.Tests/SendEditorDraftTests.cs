using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class SendEditorDraftTests
{
    [Theory]
    [InlineData("1 天", 1)]
    [InlineData("7 天", 7)]
    [InlineData("30 天", 30)]
    public void ToDeletionDate_RelativeLabel_ProducesAbsoluteDateInRange(string label, int days)
    {
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.DeletionDateLabel = label;

        var resolved = draft.ToDeletionDate();

        Assert.True(resolved > DateTimeOffset.UtcNow.AddDays(days - 1));
        Assert.True(resolved <= DateTimeOffset.UtcNow.AddDays(days + 1));
    }

    [Fact]
    public void ToDeletionDate_Custom_UsesDeletionDateButCapsAt31Days()
    {
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.DeletionDateLabel = "自定义";
        draft.DeletionDate = DateTimeOffset.UtcNow.AddDays(90);

        var now = DateTimeOffset.UtcNow;
        var resolved = draft.ToDeletionDate();

        Assert.True(resolved <= now.AddDays(31).AddSeconds(5));
    }

    [Fact]
    public void ToDeletionDate_Custom_WithDateIn20Days_ReturnsNear20Days()
    {
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.DeletionDateLabel = "自定义";
        draft.DeletionDate = DateTimeOffset.UtcNow.AddDays(20);

        var now = DateTimeOffset.UtcNow;
        var resolved = draft.ToDeletionDate();

        // Should be ~now+20, not the 7-day fallback.
        Assert.True(resolved > now.AddDays(19), "Expected ~now+20 days, not 7-day fallback");
        Assert.True(resolved <= now.AddDays(20).AddSeconds(5));
    }

    [Fact]
    public void FileBytes_DefaultsNull_AndIsSettable()
    {
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        Assert.Null(draft.FileBytes);

        draft.FileBytes = new byte[] { 9, 9 };

        Assert.Equal(new byte[] { 9, 9 }, draft.FileBytes);
    }

    [Fact]
    public void PasswordAndDisabled_DefaultEmptyFalse_Settable()
    {
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        Assert.Equal(string.Empty, draft.Password);
        Assert.False(draft.Disabled);

        draft.Password = "secret";
        draft.Disabled = true;

        Assert.Equal("secret", draft.Password);
        Assert.True(draft.Disabled);
    }
}
