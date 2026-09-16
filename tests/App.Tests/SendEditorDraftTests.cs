using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class SendEditorDraftTests
{
    [Fact]
    public void CustomDeletionCalendarChange_PreservesTimeAndFractionalSeconds()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Review time", TimeSpan.FromHours(8), "Review time", "Review time");
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.DeletionDate = DateTimeOffset.Parse("2026-09-23T14:20:31.1234567+08:00");

        draft.SelectCustomDeletionDate(DateTimeOffset.Parse("2026-09-25T00:00:00+08:00"), zone);

        Assert.Equal(DateTimeOffset.Parse("2026-09-25T14:20:31.1234567+08:00"), draft.DeletionDate);
    }

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
    public void ReplacementFile_DefaultsNull_AndExposesLength()
    {
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        Assert.Null(draft.ReplacementFile);
        Assert.Null(draft.ReplacementFileLength);

        draft.ReplacementFile = new Core.Models.SendFileSource(
            "a.bin", 3, _ => ValueTask.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3 }, writable: false)));

        Assert.Equal("a.bin", draft.ReplacementFile.FileName);
        Assert.Equal(3, draft.ReplacementFileLength);
    }

    [Fact]
    public void ClearSensitive_DropsTextPasswordNotesReplacementFileAndKey()
    {
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        draft.Text = "secret";
        draft.Password = "pw";
        draft.Notes = "note";
        draft.WrappedKey = "2.k==";
        draft.ReplacementFile = new Core.Models.SendFileSource(
            "a.bin", 1, _ => ValueTask.FromResult<Stream>(new MemoryStream(new byte[] { 1 }, writable: false)));

        draft.ClearSensitive();

        Assert.Equal(string.Empty, draft.Text);
        Assert.Equal(string.Empty, draft.Password);
        Assert.Equal(string.Empty, draft.Notes);
        Assert.Null(draft.ReplacementFile);
        Assert.Null(draft.WrappedKey);
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

    [Fact]
    public void FromExisting_TextSend_HydratesAllFields()
    {
        var expiration = DateTimeOffset.UtcNow.AddDays(3);
        var deletion = DateTimeOffset.UtcNow.AddDays(10);
        var send = new Core.Models.Send
        {
            Id = "s1",
            AccessId = "acc1",
            Type = Core.Enums.SendType.Text,
            Name = "一次性口令",
            Notes = "内部备注",
            Text = new Core.Models.SendText("秘密正文", true),
            MaxAccessCount = 5,
            AccessCount = 2,
            HideEmail = true,
            Disabled = true,
            HasPassword = true,
            ExpirationDate = expiration,
            DeletionDate = deletion,
            Key = "2.origwrapped==",
        };

        var draft = SendEditorDraft.FromExisting(send);

        Assert.Equal(SendType.Text, draft.Type);
        Assert.Equal("一次性口令", draft.Name);
        Assert.Equal("秘密正文", draft.Text);
        Assert.True(draft.HideTextByDefault);
        Assert.Equal("内部备注", draft.Notes);
        Assert.Equal(5, draft.MaxAccessCount);
        Assert.True(draft.HideEmail);
        Assert.True(draft.Disabled);
        Assert.True(draft.ExistingHasPassword);
        Assert.Equal(expiration, draft.ExpirationDate);
        Assert.Equal("自定义", draft.ExpirationDateLabel);
        Assert.True(draft.IsCustomExpiration);
        Assert.Equal(deletion, draft.DeletionDate);
        // 绝对删除日期 → 走"自定义",ToDeletionDate 使用 DeletionDate 而非相对天数默认。
        Assert.Equal("自定义", draft.DeletionDateLabel);
        Assert.Equal("2.origwrapped==", draft.WrappedKey);
        // 编辑既有 Send 默认不替换文件字节。
        Assert.Null(draft.ReplacementFile);
        // 密码只有 PBKDF2 证明、无法还原明文,故留空;更新时服务端保留原密码。
        Assert.Equal(string.Empty, draft.Password);
    }

    [Fact]
    public void FromExisting_FileSend_HydratesFileNameTypeAndKey()
    {
        var send = new Core.Models.Send
        {
            Id = "s2",
            AccessId = "acc2",
            Type = Core.Enums.SendType.File,
            Name = "季度报告",
            File = new Core.Models.SendFile("报告.pdf", 2048, "2 KB", "f1"),
            DeletionDate = DateTimeOffset.UtcNow.AddDays(7),
            Key = "2.filewrapped==",
        };

        var draft = SendEditorDraft.FromExisting(send);

        Assert.Equal(SendType.File, draft.Type);
        Assert.Equal("季度报告", draft.Name);
        Assert.Equal("报告.pdf", draft.FileName);
        Assert.Equal("2.filewrapped==", draft.WrappedKey);
        Assert.Null(draft.ReplacementFile);
    }
}
