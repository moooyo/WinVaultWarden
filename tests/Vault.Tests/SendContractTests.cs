using Core.Enums;
using Core.Models;
using Core.Services;
using Xunit;

namespace Vault.Tests;

public class SendContractTests
{
    [Fact]
    public void SendDraftModel_DefaultsAreCreateText()
    {
        var draft = new SendDraftModel
        {
            Type = SendType.Text,
            Name = "n",
            DeletionDate = DateTimeOffset.UtcNow.AddDays(7),
        };

        Assert.Null(draft.Id);
        Assert.Equal(SendType.Text, draft.Type);
        Assert.Equal("n", draft.Name);
        Assert.IsType<SendPasswordChange.Keep>(draft.PasswordChange);
        Assert.False(draft.Disabled);
        Assert.False(draft.HideEmail);
        Assert.Null(draft.MaxAccessCount);
        Assert.Null(draft.ExpirationDate);
    }

    [Fact]
    public void SendAccessResult_HoldsDecryptedProjection()
    {
        var seed = new byte[16];
        var result = new SendAccessResult
        {
            Type = SendType.File,
            Name = "doc",
            FileName = "secret.pdf",
            FileDownloadUrl = "https://vault.example/dl",
            AccessId = "acc-1",
            Seed = seed,
        };

        Assert.Equal(SendType.File, result.Type);
        Assert.Equal("doc", result.Name);
        Assert.Equal("secret.pdf", result.FileName);
        Assert.Equal("https://vault.example/dl", result.FileDownloadUrl);
        Assert.Equal("acc-1", result.AccessId);
        Assert.Same(seed, result.Seed);
    }

    [Fact]
    public void Interfaces_AreImplementableShapes()
    {
        // Compile-time assertion that the interface members exist with the contract signatures.
        Assert.Contains(typeof(ISendService).GetMethod("GetSendsAsync")!.ReturnType.Name, "Task`1");
        Assert.NotNull(typeof(ISendWriteService).GetMethod("SaveTextSendAsync"));
        Assert.NotNull(typeof(ISendWriteService).GetMethod("SaveFileSendAsync"));
        Assert.NotNull(typeof(ISendWriteService).GetMethod("UpdateFileSendMetadataAsync"));
        Assert.NotNull(typeof(ISendWriteService).GetMethod("DeleteSendAsync"));
        Assert.NotNull(typeof(ISendWriteService).GetMethod("RemovePasswordAsync"));
        Assert.NotNull(typeof(ISendAccessService).GetMethod("AccessAsync"));
        Assert.NotNull(typeof(ISendAccessService).GetMethod("DownloadFileAsync"));
    }

    [Fact]
    public void SendPasswordChange_IsExplicitThreeState()
    {
        // Task 11: Keep/Set/Remove 必须是显式类型,不能用 null/空字符串暗号代替。
        SendPasswordChange keep = new SendPasswordChange.Keep();
        SendPasswordChange set = new SendPasswordChange.Set("p4ssw0rd");
        SendPasswordChange remove = new SendPasswordChange.Remove();

        Assert.IsType<SendPasswordChange.Keep>(keep);
        Assert.Equal("p4ssw0rd", Assert.IsType<SendPasswordChange.Set>(set).Password);
        Assert.IsType<SendPasswordChange.Remove>(remove);
        Assert.Throws<ArgumentException>(() => new SendPasswordChange.Set(""));
        Assert.Throws<ArgumentException>(() => new SendPasswordChange.Set("   "));
    }
}
