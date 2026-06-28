using Api.Dtos;
using Core.Enums;
using Core.Models;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class SendWriteServiceTests
{
    private static readonly byte[] UserKeyBytes = Enumerable.Range(0, 64).Select(i => (byte)(i + 2)).ToArray();

    private static (SendWriteService service, FakeSendApiClient api, SendCryptoService crypto, SymmetricCryptoKey userKey) NewService()
    {
        var userKey = new SymmetricCryptoKey(UserKeyBytes);
        var session = new VaultSession();
        session.SetUnlockedKey(userKey);
        var crypto = new SendCryptoService(new CryptoService());
        var api = new FakeSendApiClient();
        return (new SendWriteService(api, crypto, session), api, crypto, userKey);
    }

    private static SendDraftModel TextDraft(string? password = null, string? id = null) => new()
    {
        Id = id,
        Type = SendType.Text,
        Name = "My Send",
        Notes = "note text",
        TextContent = "the secret",
        TextHidden = true,
        DeletionDate = DateTimeOffset.UtcNow.AddDays(7),
        ExpirationDate = DateTimeOffset.UtcNow.AddDays(3),
        MaxAccessCount = 4,
        Disabled = false,
        HideEmail = true,
        Password = password,
    };

    [Fact]
    public async Task SaveTextSend_New_EncryptsFieldsAndCreates()
    {
        var (service, api, _, _) = NewService();

        await service.SaveTextSendAsync(TextDraft(), TestContext.Current.CancellationToken);

        Assert.Equal("create-text", Assert.Single(api.Calls));
        var req = api.LastCreateText!;
        Assert.Equal((int)SendType.Text, req.Type);
        // 字段必须是 EncString (type-2 前缀),绝不能是明文。
        Assert.StartsWith("2.", req.Name);
        Assert.NotEqual("My Send", req.Name);
        Assert.StartsWith("2.", req.Notes);
        Assert.NotNull(req.Text);
        Assert.StartsWith("2.", req.Text!.Text);
        Assert.NotEqual("the secret", req.Text.Text);
        Assert.True(req.Text.Hidden);
        // key 字段是 seed 用 userKey 包裹的 EncString。
        Assert.StartsWith("2.", req.Key);
        // deletionDate 必填,ISO8601 round-trip。
        Assert.False(string.IsNullOrEmpty(req.DeletionDate));
        Assert.NotNull(req.ExpirationDate);
        Assert.Equal(4, req.MaxAccessCount);
        Assert.True(req.HideEmail);
        Assert.Null(req.File);
        Assert.Null(req.FileLength);
    }

    [Fact]
    public async Task SaveTextSend_NoPassword_PasswordProofNull()
    {
        var (service, api, _, _) = NewService();

        await service.SaveTextSendAsync(TextDraft(password: null), TestContext.Current.CancellationToken);

        Assert.Null(api.LastCreateText!.Password);
    }

    [Fact]
    public async Task SaveTextSend_WithPassword_SendsProofNotPlaintext()
    {
        var (service, api, _, _) = NewService();

        await service.SaveTextSendAsync(TextDraft(password: "hunter2"), TestContext.Current.CancellationToken);

        var proof = api.LastCreateText!.Password;
        Assert.False(string.IsNullOrEmpty(proof));
        Assert.NotEqual("hunter2", proof);
        // 是 base64(PBKDF2 32 字节)。
        Assert.Equal(32, Convert.FromBase64String(proof!).Length);
    }

    [Fact]
    public async Task SaveTextSend_Existing_Updates()
    {
        var (service, api, _, _) = NewService();

        await service.SaveTextSendAsync(TextDraft(id: "s-7"), TestContext.Current.CancellationToken);

        Assert.Equal("update", Assert.Single(api.Calls));
        Assert.Equal("s-7", api.LastUpdateId);
        Assert.Equal((int)SendType.Text, api.LastUpdate!.Type);
    }

    [Fact]
    public async Task SaveFileSend_EncryptsBufferAndUploadsToReturnedUrl()
    {
        var (service, api, _, _) = NewService();
        // SendFileUploadV2Response 是位置记录,须用位置构造函数语法。
        var sendResponse = new SendResponseDto(
            Id: "s-9", AccessId: "acc-9", Type: 2, Name: "", Notes: null,
            Text: null, File: null, Key: null, MaxAccessCount: null, AccessCount: 0,
            Password: null, AuthType: 0, Disabled: false, HideEmail: false,
            RevisionDate: null, ExpirationDate: null,
            DeletionDate: DateTimeOffset.UtcNow.AddDays(7), Object: null);
        api.CreateFileV2Result = new SendFileUploadV2Response(
            FileUploadType: 0,
            Object: null,
            Url: "/sends/s-9/file/f-2",
            SendResponse: sendResponse);
        var fileBytes = new byte[] { 10, 20, 30, 40, 50 };
        var draft = new SendDraftModel
        {
            Type = SendType.File,
            Name = "doc",
            FileName = "report.pdf",
            DeletionDate = DateTimeOffset.UtcNow.AddDays(7),
        };

        await service.SaveFileSendAsync(draft, fileBytes, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "create-file-v2", "upload" }, api.Calls);
        var req = api.LastCreateFileV2!;
        Assert.Equal((int)SendType.File, req.Type);
        Assert.NotNull(req.File);
        Assert.StartsWith("2.", req.File!.FileName);
        Assert.NotEqual("report.pdf", req.File.FileName);
        Assert.Null(req.Text);
        // fileLength == EncArrayBuffer 长度 == 1 + 16 + 32 + ceil 后的密文长度。
        Assert.NotNull(req.FileLength);
        Assert.Equal(api.LastUploadBuffer!.Length, req.FileLength);
        // upload 调用到的是 create 返回的 Url,文件名是加密后的 fileName。
        Assert.Equal("/sends/s-9/file/f-2", api.LastUploadUrl);
        Assert.Equal(req.File.FileName, api.LastUploadFileName);
        // 缓冲区头字节为 EncArrayBuffer 版本 2。
        Assert.Equal((byte)2, api.LastUploadBuffer![0]);
        Assert.True(api.LastUploadBuffer.Length >= 1 + 16 + 32);
    }

    [Fact]
    public async Task DeleteSend_CallsDelete()
    {
        var (service, api, _, _) = NewService();

        await service.DeleteSendAsync("s-3", TestContext.Current.CancellationToken);

        Assert.Equal("delete", Assert.Single(api.Calls));
        Assert.Equal("s-3", api.LastDeleteId);
    }

    [Fact]
    public async Task RemovePassword_CallsRemoveAndReturnsDecryptedSend()
    {
        var (service, api, crypto, userKey) = NewService();
        var seed = crypto.GenerateSeed();
        var cryptoKey = crypto.DeriveCryptoKey(seed);
        api.RemovePasswordResult = new SendResponseDto(
            Id: "s-4",
            AccessId: "acc-4",
            Type: 1,
            Name: crypto.EncryptField("Named", cryptoKey),
            Notes: null,
            Text: new SendTextDto(Text: crypto.EncryptField("body", cryptoKey), Hidden: false),
            File: null,
            Key: crypto.WrapSeed(seed, userKey),
            MaxAccessCount: null,
            AccessCount: 0,
            Password: null,
            AuthType: 0,
            Disabled: false,
            HideEmail: false,
            RevisionDate: null,
            ExpirationDate: null,
            DeletionDate: DateTimeOffset.UtcNow.AddDays(2),
            Object: "send");

        var send = await service.RemovePasswordAsync("s-4", TestContext.Current.CancellationToken);

        Assert.Equal("remove-password", Assert.Single(api.Calls));
        Assert.Equal("s-4", api.LastRemovePasswordId);
        Assert.Equal("Named", send.Name);
        Assert.False(send.HasPassword);
    }

    [Fact]
    public async Task SaveTextSend_WhenLocked_Throws()
    {
        var crypto = new SendCryptoService(new CryptoService());
        var service = new SendWriteService(new FakeSendApiClient(), crypto, new VaultSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveTextSendAsync(TextDraft(), TestContext.Current.CancellationToken));
    }
}
