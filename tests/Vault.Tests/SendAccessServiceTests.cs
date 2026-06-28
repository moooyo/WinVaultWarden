using Api.Dtos;
using Core.Enums;
using Core.Models;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class SendAccessServiceTests
{
    private static (SendAccessService service, FakeSendApiClient api, SendCryptoService crypto) NewService()
    {
        var crypto = new SendCryptoService(new CryptoService());
        var api = new FakeSendApiClient();
        return (new SendAccessService(api, crypto), api, crypto);
    }

    [Fact]
    public async Task Access_TextSend_ParsesUrlDerivesKeyAndDecrypts()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        var cryptoKey = crypto.DeriveCryptoKey(seed);
        var url = crypto.BuildShareUrl("https://vault.example", "acc-1", seed);
        api.AccessResult = new SendAccessResponseDto(
            Id: "s-1",
            Type: 0,
            Name: crypto.EncryptField("Shared Note", cryptoKey),
            Text: new SendTextDto(Text: crypto.EncryptField("hello world", cryptoKey), Hidden: false),
            File: null,
            ExpirationDate: null,
            CreatorIdentifier: null,
            Object: "send-access");

        var result = await service.AccessAsync(url, password: null, TestContext.Current.CancellationToken);

        Assert.Equal("acc-1", api.LastAccessId);
        Assert.Null(api.LastAccessPasswordProof);
        Assert.Equal(SendType.Text, result.Type);
        Assert.Equal("Shared Note", result.Name);
        Assert.Equal("hello world", result.TextContent);
        Assert.Equal("acc-1", result.AccessId);
        Assert.Equal(seed, result.Seed);
    }

    [Fact]
    public async Task Access_WithPassword_SendsProofNotPlaintext()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        var cryptoKey = crypto.DeriveCryptoKey(seed);
        var url = crypto.BuildShareUrl("https://vault.example", "acc-2", seed);
        api.AccessResult = new SendAccessResponseDto(
            Id: "s-2",
            Type: 0,
            Name: crypto.EncryptField("n", cryptoKey),
            Text: new SendTextDto(Text: crypto.EncryptField("t", cryptoKey), Hidden: false),
            File: null,
            ExpirationDate: null,
            CreatorIdentifier: null,
            Object: "send-access");

        await service.AccessAsync(url, password: "letmein", TestContext.Current.CancellationToken);

        var proof = api.LastAccessPasswordProof;
        Assert.False(string.IsNullOrEmpty(proof));
        Assert.NotEqual("letmein", proof);
        Assert.Equal(32, Convert.FromBase64String(proof!).Length);
    }

    [Fact]
    public async Task Access_FileSend_PopulatesFileNameAndDownloadInfo()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        var cryptoKey = crypto.DeriveCryptoKey(seed);
        var url = crypto.BuildShareUrl("https://vault.example", "acc-3", seed);
        api.AccessResult = new SendAccessResponseDto(
            Id: "s-3",
            Type: 1,
            Name: crypto.EncryptField("File Send", cryptoKey),
            Text: null,
            File: new SendFileDto(Id: "f-3", FileName: crypto.EncryptField("data.bin", cryptoKey), Size: 999L, SizeName: "999 Bytes"),
            ExpirationDate: null,
            CreatorIdentifier: null,
            Object: "send-access");

        var result = await service.AccessAsync(url, password: null, TestContext.Current.CancellationToken);

        Assert.Equal(SendType.File, result.Type);
        Assert.Equal("File Send", result.Name);
        Assert.Equal("data.bin", result.FileName);
        // 下载信息由 SendId+FileId 提供(真实服务端两步协议);FileDownloadUrl 置空。
        Assert.Equal("s-3", result.SendId);
        Assert.Equal("f-3", result.FileId);
        Assert.Equal(seed, result.Seed);
    }

    [Fact]
    public async Task DownloadFile_DecryptsEncArrayBuffer()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        var cryptoKey = crypto.DeriveCryptoKey(seed);
        var plaintext = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        api.DownloadBytes = crypto.EncryptToBuffer(plaintext, cryptoKey);

        var accessed = new SendAccessResult
        {
            Type = SendType.File,
            Name = "f",
            FileName = "data.bin",
            FileDownloadUrl = "https://vault.example/dl/f-3",
            AccessId = "acc-3",
            Seed = seed,
        };

        var bytes = await service.DownloadFileAsync(accessed, TestContext.Current.CancellationToken);

        Assert.Equal("https://vault.example/dl/f-3", api.LastDownloadUrl);
        Assert.Equal(plaintext, bytes);
    }

    [Fact]
    public async Task Access_WrongPassword_PropagatesServerError()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        var url = crypto.BuildShareUrl("https://vault.example", "acc-4", seed);
        api.AccessException = new HttpRequestException("401 Unauthorized");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.AccessAsync(url, password: "wrong", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Access_MalformedUrl_Throws()
    {
        var (service, _, _) = NewService();

        await Assert.ThrowsAsync<FormatException>(() =>
            service.AccessAsync("https://vault.example/not-a-send-url", password: null, TestContext.Current.CancellationToken));
    }
}
