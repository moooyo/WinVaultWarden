using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
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
        var endpoints = ServerEndpoints.FromWebVault(serverUrl);
        session.BeginAccountSession("test-account", "test@test.com", endpoints);
        session.SetUnlockedKey(userKey);
        session.SetAccount(new Core.Models.AccountInfo("test-account", "test@test.com", endpoints, "T", "PBKDF2"));
        var crypto = new SendCryptoService(new CryptoService());
        var api = new FakeSendApiClient();
        return (new SendService(FakeVaultApiClientProvider.ForAuthenticated<ISendApiClient>(api), crypto, session), api, crypto, userKey);
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
            AuthType: (int)SendAuthType.None,
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
    public async Task GetSends_UnsupportedItemSend_FailsClosed()
    {
        var (service, api, crypto, userKey) = NewService();
        api.ListResult = new SendListResponse(
            Data: [BuildEncryptedDto(
                crypto,
                userKey,
                type: 2,
                name: "Item Send",
                notes: null,
                text: null,
                fileName: null)],
            Object: "list");

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.GetSendsAsync(TestContext.Current.CancellationToken));
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
    public async Task GetSends_PasswordAuthTypeWithoutVerifier_FailsClosedAsProtected()
    {
        var (service, api, crypto, userKey) = NewService();
        var dto = BuildEncryptedDto(crypto, userKey, 1, "n", null, null, "file.bin") with
        {
            Password = null,
            AuthType = 1,
        };
        api.ListResult = new SendListResponse(Data: [dto], Object: "list");

        var send = Assert.Single(await service.GetSendsAsync(TestContext.Current.CancellationToken));

        Assert.True(send.HasPassword);
    }

    [Fact]
    public async Task GetSends_WhenLocked_Throws()
    {
        var crypto = new SendCryptoService(new CryptoService());
        var service = new SendService(FakeVaultApiClientProvider.ForAuthenticated<ISendApiClient>(new FakeSendApiClient()), crypto, new VaultSession());

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

    [Theory]
    [InlineData("https://vault.bitwarden.com", "https://send.bitwarden.com/#/send/")]
    [InlineData("https://vault.bitwarden.eu", "https://vault.bitwarden.eu/#/send/")]
    public async Task GetSends_OfficialEnvironment_UsesCatalogSendWebEndpoint(
        string webVault,
        string expectedPrefix)
    {
        var (service, api, crypto, userKey) = NewService(webVault);
        api.ListResult = new SendListResponse(
            Data: [BuildEncryptedDto(crypto, userKey, 0, "Cloud Send", null, "body", null)],
            Object: "list");

        var send = Assert.Single(await service.GetSendsAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith(expectedPrefix, send.ShareUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSendsAsync_SkipsCorruptSend_KeepsOthers()
    {
        // R7:单个损坏的 Send(此处 Key=null → UnwrapSeed 抛异常)不得拖垮整表,其余正常 Send 仍返回。
        var (service, api, crypto, userKey) = NewService();
        var good = BuildEncryptedDto(crypto, userKey, 1, "Good", null, "hi", null);
        var bad = good with { Id = "bad", Key = null! };
        api.ListResult = new SendListResponse(Data: new[] { bad, good }, Object: "list");

        var sends = await service.GetSendsAsync(TestContext.Current.CancellationToken);

        var single = Assert.Single(sends);
        Assert.Equal("Good", single.Name);
    }

    [Fact]
    public async Task ObserveSendsAsync_PreservesCorruptRawIdentityForReconciliation()
    {
        var (service, api, crypto, userKey) = NewService();
        var good = BuildEncryptedDto(crypto, userKey, 1, "Good", null, null, "good.bin");
        var bad = good with { Id = "corrupt-send", Key = null! };
        api.ListResult = new SendListResponse(Data: [bad, good], Object: "list");

        var snapshot = await service.ObserveSendsAsync(TestContext.Current.CancellationToken);

        Assert.True(snapshot.IsComplete);
        Assert.Equal(2, snapshot.Observations.Count);
        var corrupt = Assert.Single(snapshot.Observations, item => item.Id == "corrupt-send");
        Assert.Equal(SendDecryptStatus.Corrupt, corrupt.DecryptStatus);
        Assert.Null(corrupt.Value);
        Assert.False(string.IsNullOrEmpty(corrupt.RawMutationWitness));
    }

    [Fact]
    public async Task ObserveSendsAsync_NullOrEmptyRawIdentity_MarksSnapshotIncomplete()
    {
        var (service, api, crypto, userKey) = NewService();
        var valid = BuildEncryptedDto(crypto, userKey, 0, "Valid", null, "body", null);
        api.ListResult = new SendListResponse(
            Data: [null!, valid with { Id = string.Empty }, valid],
            Object: "list");

        var snapshot = await service.ObserveSendsAsync(TestContext.Current.CancellationToken);

        Assert.False(snapshot.IsComplete);
        Assert.Equal("s-1", Assert.Single(snapshot.Observations).Id);
    }

    [Theory]
    [InlineData(0, SendAuthType.Email)]
    [InlineData(1, SendAuthType.Password)]
    [InlineData(2, SendAuthType.None)]
    [InlineData(42, SendAuthType.Unknown)]
    public async Task GetSends_MapsWireAuthenticationMode(int rawAuthType, SendAuthType expected)
    {
        var (service, api, crypto, userKey) = NewService();
        var dto = BuildEncryptedDto(crypto, userKey, 0, "Auth", null, "body", null) with
        {
            AuthType = rawAuthType,
        };
        api.ListResult = new SendListResponse(Data: [dto], Object: "list");

        var send = Assert.Single(await service.GetSendsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected, send.AuthType);
        Assert.Equal(rawAuthType, send.RawAuthType);
    }

    [Theory]
    [InlineData(null, null, SendAuthType.None, false)]
    [InlineData("legacy-password-verifier", null, SendAuthType.Password, true)]
    [InlineData(null, "one@example.test", SendAuthType.Email, false)]
    [InlineData("legacy-password-verifier", "one@example.test", SendAuthType.Unknown, true)]
    public async Task GetSends_MissingAuthenticationMode_UsesLegacyPasswordContract(
        string? passwordVerifier,
        string? emails,
        SendAuthType expected,
        bool expectedHasPassword)
    {
        var (service, api, crypto, userKey) = NewService();
        var dto = BuildEncryptedDto(crypto, userKey, 0, "Auth", null, "body", null) with
        {
            AuthType = null,
            Password = passwordVerifier,
            Emails = emails,
        };
        api.ListResult = new SendListResponse(Data: [dto], Object: "list");

        var send = Assert.Single(await service.GetSendsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected, send.AuthType);
        Assert.Equal(expectedHasPassword, send.HasPassword);
        Assert.Equal(int.MinValue, send.RawAuthType);
    }

    [Theory]
    [InlineData((int)SendAuthType.Email, "verifier", null)]
    [InlineData((int)SendAuthType.Password, null, "one@example.test")]
    [InlineData((int)SendAuthType.None, "verifier", null)]
    [InlineData((int)SendAuthType.None, null, "one@example.test")]
    [InlineData(99, "verifier", null)]
    public async Task GetSends_ContradictoryAuthenticationEvidence_IsUnknown(
        int rawAuthType,
        string? passwordVerifier,
        string? emails)
    {
        var (service, api, crypto, userKey) = NewService();
        var dto = BuildEncryptedDto(crypto, userKey, 0, "Auth", null, "body", null) with
        {
            AuthType = rawAuthType,
            Password = passwordVerifier,
            Emails = emails,
        };
        api.ListResult = new SendListResponse(Data: [dto], Object: "list");

        var send = Assert.Single(await service.GetSendsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SendAuthType.Unknown, send.AuthType);
        Assert.Equal(rawAuthType, send.RawAuthType);
    }

    [Fact]
    public async Task GetSends_CarriesWrappedKey_ForFileSendEditing()
    {
        // F1a:领域 Send 必须保留服务端的 wrapped key(akey),供编辑文件 Send 时复用原 seed,
        // 否则更新时改写 akey 会让服务端上以旧 seed 加密的文件与文件名永久无法解密。
        var (service, api, crypto, userKey) = NewService();
        var dto = BuildEncryptedDto(crypto, userKey, 1, "File Send", null, null, "report.pdf");
        api.ListResult = new SendListResponse(Data: new[] { dto }, Object: "list");

        var send = Assert.Single(await service.GetSendsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(dto.Key, send.Key);
        Assert.False(string.IsNullOrEmpty(send.Key));
        // 复用该 wrapped key 能解回一个有效 seed。
        var seed = crypto.UnwrapSeed(send.Key!, userKey);
        Assert.Equal(16, seed.Length);
    }
}
