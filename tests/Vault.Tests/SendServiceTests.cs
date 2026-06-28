using Api.Dtos;
using Core.Enums;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class SendServiceTests
{
    private static readonly byte[] UserKeyBytes = Enumerable.Range(0, 64).Select(i => (byte)(i + 1)).ToArray();

    private static (SendService service, FakeSendApiClient api, SendCryptoService crypto, SymmetricCryptoKey userKey) NewService(
        string serverUrl = "https://vault.test")
    {
        var userKey = new SymmetricCryptoKey(UserKeyBytes);
        var session = new VaultSession();
        session.SetUnlockedKey(userKey);
        session.SetAccount(new Core.Models.AccountInfo("test@test.com", serverUrl, "T", "PBKDF2"));
        var crypto = new SendCryptoService(new CryptoService());
        var api = new FakeSendApiClient();
        return (new SendService(api, crypto, session), api, crypto, userKey);
    }

    // 用真实 SendCryptoService 把明文加密成服务端形态的 SendResponseDto。
    private static SendResponseDto BuildEncryptedDto(
        SendCryptoService crypto, SymmetricCryptoKey userKey, int type,
        string name, string? notes, string? text, string? fileName)
    {
        var seed = crypto.GenerateSeed();
        return BuildEncryptedDtoWithSeed(crypto, userKey, type, name, notes, text, fileName, seed);
    }

    private static SendResponseDto BuildEncryptedDtoWithSeed(
        SendCryptoService crypto, SymmetricCryptoKey userKey, int type,
        string name, string? notes, string? text, string? fileName, byte[] seed)
    {
        var cryptoKey = crypto.DeriveCryptoKey(seed);
        return new SendResponseDto(
            Id: "s-1",
            AccessId: "acc-1",
            Type: type,
            Name: crypto.EncryptField(name, cryptoKey),
            Notes: notes is null ? null : crypto.EncryptField(notes, cryptoKey),
            Key: crypto.WrapSeed(seed, userKey),
            Text: text is null ? null : new SendTextDto(Text: crypto.EncryptField(text, cryptoKey), Hidden: true),
            File: fileName is null ? null : new SendFileDto(
                Id: "f-1",
                FileName: crypto.EncryptField(fileName, cryptoKey),
                Size: 123L,
                SizeName: "123 Bytes"),
            MaxAccessCount: 5,
            AccessCount: 2,
            Password: null,
            AuthType: 0,
            Disabled: false,
            HideEmail: true,
            RevisionDate: null,
            ExpirationDate: null,
            DeletionDate: DateTimeOffset.UtcNow.AddDays(3),
            Object: "send");
    }

    [Fact]
    public async Task GetSends_DecryptsTextSendFields()
    {
        var (service, api, crypto, userKey) = NewService();
        api.ListResult = new SendListResponse(
            Data: new[] { BuildEncryptedDto(crypto, userKey, 0, "My Text Send", "a note", "secret body", null) },
            Object: "list");

        var sends = await service.GetSendsAsync(TestContext.Current.CancellationToken);

        var send = Assert.Single(sends);
        Assert.Equal("s-1", send.Id);
        Assert.Equal("acc-1", send.AccessId);
        Assert.Equal(SendType.Text, send.Type);
        Assert.Equal("My Text Send", send.Name);
        Assert.Equal("a note", send.Notes);
        Assert.NotNull(send.Text);
        Assert.Equal("secret body", send.Text!.Content);
        Assert.True(send.Text.Hidden);
        Assert.Null(send.File);
        Assert.Equal(5, send.MaxAccessCount);
        Assert.Equal(2, send.AccessCount);
        Assert.True(send.HideEmail);
        Assert.False(send.HasPassword);
    }

    [Fact]
    public async Task GetSends_DecryptsFileSendNameAndMapsType()
    {
        var (service, api, crypto, userKey) = NewService();
        api.ListResult = new SendListResponse(
            Data: new[] { BuildEncryptedDto(crypto, userKey, 1, "My File Send", null, null, "report.pdf") },
            Object: "list");

        var sends = await service.GetSendsAsync(TestContext.Current.CancellationToken);

        var send = Assert.Single(sends);
        Assert.Equal(SendType.File, send.Type);
        Assert.Equal("My File Send", send.Name);
        Assert.NotNull(send.File);
        Assert.Equal("report.pdf", send.File!.FileName);
        Assert.Equal("f-1", send.File.FileId);
        Assert.Equal(123, send.File.Size);
        Assert.Null(send.Text);
    }

    [Fact]
    public async Task GetSends_PasswordPresent_SetsHasPassword()
    {
        var (service, api, crypto, userKey) = NewService();
        var dto = BuildEncryptedDto(crypto, userKey, 1, "n", null, "t", null);
        var dtoWithPassword = dto with { Password = "some-proof-hash" };
        api.ListResult = new SendListResponse(Data: new[] { dtoWithPassword }, Object: "list");

        var sends = await service.GetSendsAsync(TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(sends).HasPassword);
    }

    [Fact]
    public async Task GetSends_WhenLocked_Throws()
    {
        var crypto = new SendCryptoService(new CryptoService());
        var service = new SendService(new FakeSendApiClient(), crypto, new VaultSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetSendsAsync(TestContext.Current.CancellationToken));
    }

    // C1 回归：ShareUrl 必须包含 seed；TryParseShareUrl 能恢复原始 accessId 和 seed。
    [Fact]
    public async Task GetSends_ShareUrl_ContainsSeedAndRoundTrips()
    {
        var (service, api, crypto, userKey) = NewService("https://vault.test");
        var knownSeed = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var dto = BuildEncryptedDtoWithSeed(crypto, userKey, 0, "Share Test", null, "body", null, knownSeed);
        api.ListResult = new SendListResponse(Data: new[] { dto }, Object: "list");

        var sends = await service.GetSendsAsync(TestContext.Current.CancellationToken);

        var send = Assert.Single(sends);
        Assert.False(string.IsNullOrEmpty(send.ShareUrl), "ShareUrl must not be empty");
        Assert.True(
            crypto.TryParseShareUrl(send.ShareUrl, out var parsedAccessId, out var parsedSeed),
            $"TryParseShareUrl failed on: {send.ShareUrl}");
        Assert.Equal("acc-1", parsedAccessId);
        Assert.Equal(knownSeed, parsedSeed);
    }
}
