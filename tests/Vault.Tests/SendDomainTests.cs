using Core.Enums;
using Core.Models;
using Xunit;

namespace Vault.Tests;

public class SendDomainTests
{
    [Fact]
    public void SendType_HasBitwardenWireValues()
    {
        Assert.Equal(1, (int)SendType.Text);
        Assert.Equal(2, (int)SendType.File);
    }

    [Fact]
    public void Send_DefaultConstructs_WithEmptyStringsAndNulls()
    {
        var send = new Send();

        Assert.Equal(string.Empty, send.Id);
        Assert.Equal(string.Empty, send.AccessId);
        Assert.Equal(string.Empty, send.Name);
        Assert.Null(send.Notes);
        Assert.Null(send.Text);
        Assert.Null(send.File);
        Assert.Null(send.MaxAccessCount);
        Assert.Equal(0, send.AccessCount);
        Assert.Null(send.ExpirationDate);
        Assert.False(send.Disabled);
        Assert.False(send.HideEmail);
        Assert.False(send.HasPassword);
    }

    [Fact]
    public void Send_CanRepresentDecryptedTextSend()
    {
        var send = new Send
        {
            Id = "s1",
            AccessId = "abc123",
            Type = SendType.Text,
            Name = "shared note",
            Notes = "internal",
            Text = new SendText("the secret payload", Hidden: true),
            MaxAccessCount = 5,
            AccessCount = 2,
            ExpirationDate = DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            DeletionDate = DateTimeOffset.Parse("2026-07-29T00:00:00Z"),
            Disabled = false,
            HideEmail = true,
            HasPassword = true,
        };

        Assert.Equal(SendType.Text, send.Type);
        Assert.Equal("the secret payload", send.Text!.Content);
        Assert.True(send.Text.Hidden);
        Assert.Null(send.File);
        Assert.Equal(5, send.MaxAccessCount);
        Assert.True(send.HasPassword);
        Assert.Equal(DateTimeOffset.Parse("2026-07-29T00:00:00Z"), send.DeletionDate);
    }

    [Fact]
    public void Send_CanRepresentDecryptedFileSend()
    {
        var send = new Send
        {
            Id = "s2",
            AccessId = "def456",
            Type = SendType.File,
            Name = "report.pdf",
            File = new SendFile("report.pdf", Size: 12_345, SizeName: "12.06 KB", FileId: "file-1"),
            DeletionDate = DateTimeOffset.Parse("2026-07-10T00:00:00Z"),
        };

        Assert.Equal(SendType.File, send.Type);
        Assert.Null(send.Text);
        Assert.Equal("report.pdf", send.File!.FileName);
        Assert.Equal(12_345, send.File.Size);
        Assert.Equal("12.06 KB", send.File.SizeName);
        Assert.Equal("file-1", send.File.FileId);
    }

    [Fact]
    public void SendDraftModel_CreateDraft_HasNullIdAndRequiredDeletionDate()
    {
        var deletion = DateTimeOffset.Parse("2026-07-15T00:00:00Z");
        var draft = new SendDraftModel
        {
            Id = null,
            Type = SendType.Text,
            Name = "draft",
            Notes = "n",
            TextContent = "payload",
            TextHidden = false,
            MaxAccessCount = 3,
            ExpirationDate = null,
            DeletionDate = deletion,
            Disabled = false,
            HideEmail = false,
            Password = "secret",
        };

        Assert.Null(draft.Id);
        Assert.Equal(SendType.Text, draft.Type);
        Assert.Equal("payload", draft.TextContent);
        Assert.Equal(deletion, draft.DeletionDate);
        Assert.Equal("secret", draft.Password);
    }

    [Fact]
    public void SendDraftModel_FileDraft_CarriesFileName()
    {
        var draft = new SendDraftModel
        {
            Type = SendType.File,
            Name = "f",
            FileName = "report.pdf",
            DeletionDate = DateTimeOffset.Parse("2026-07-15T00:00:00Z"),
        };

        Assert.Equal(SendType.File, draft.Type);
        Assert.Equal("report.pdf", draft.FileName);
        Assert.Null(draft.TextContent);
    }

    [Fact]
    public void SendAccessResult_CarriesAccessIdAndSeedForFileDecrypt()
    {
        var seed = new byte[] { 1, 2, 3, 4 };
        var result = new SendAccessResult
        {
            Type = SendType.File,
            Name = "report.pdf",
            Notes = "n",
            TextContent = null,
            FileName = "report.pdf",
            FileDownloadUrl = "https://cdn.example/file",
            AccessId = "def456",
            Seed = seed,
        };

        Assert.Equal(SendType.File, result.Type);
        Assert.Equal("def456", result.AccessId);
        Assert.Same(seed, result.Seed);
        Assert.Equal("https://cdn.example/file", result.FileDownloadUrl);
    }
}
