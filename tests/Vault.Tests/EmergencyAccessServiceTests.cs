using Api;
using Api.Dtos;
using Core.Models;
using Crypto;
using Vault;
using Xunit;
using System.Security.Cryptography;

namespace Vault.Tests;

public class EmergencyAccessServiceTests
{
    private sealed class FakeEaApi : IEmergencyAccessApiClient
    {
        public List<string> Calls { get; } = new();
        public EmergencyAccessInviteRequest? LastInvite { get; private set; }
        public EmergencyAccessConfirmRequest? LastConfirm { get; private set; }
        public string? PublicKeyB64 { get; set; }
        public ListResponse<EmergencyAccessGranteeDetailsDto> Trusted { get; set; } = new(Array.Empty<EmergencyAccessGranteeDetailsDto>(), null, null);

        // Task 6: grantee-side support
        public EmergencyAccessViewResponse ViewResult { get; set; } = new();
        public EmergencyAccessTakeoverResponse TakeoverResult { get; set; } = new();
        public EmergencyAccessPasswordRequest? LastPassword { get; private set; }

        public Task<ListResponse<EmergencyAccessGranteeDetailsDto>> GetTrustedAsync(CancellationToken ct = default){ Calls.Add("trusted"); return Task.FromResult(Trusted); }
        public Task<ListResponse<EmergencyAccessGrantorDetailsDto>> GetGrantedAsync(CancellationToken ct = default){ Calls.Add("granted"); return Task.FromResult(new ListResponse<EmergencyAccessGrantorDetailsDto>(Array.Empty<EmergencyAccessGrantorDetailsDto>(), null, null)); }
        public Task<PublicKeyResponse> GetPublicKeyAsync(string userId, CancellationToken ct = default){ Calls.Add($"pubkey:{userId}"); return Task.FromResult(new PublicKeyResponse{ UserId = userId, PublicKey = PublicKeyB64 }); }
        public Task InviteAsync(EmergencyAccessInviteRequest request, CancellationToken ct = default){ Calls.Add("invite"); LastInvite = request; return Task.CompletedTask; }
        public Task ReinviteAsync(string id, CancellationToken ct = default){ Calls.Add($"reinvite:{id}"); return Task.CompletedTask; }
        public Task AcceptAsync(string id, EmergencyAccessAcceptRequest request, CancellationToken ct = default){ Calls.Add($"accept:{id}"); return Task.CompletedTask; }
        public Task<EmergencyAccessDto> ConfirmAsync(string id, EmergencyAccessConfirmRequest request, CancellationToken ct = default){ Calls.Add($"confirm:{id}"); LastConfirm = request; return Task.FromResult(new EmergencyAccessDto{ Id = id, Status = 2 }); }
        public Task<EmergencyAccessDto> UpdateAsync(string id, EmergencyAccessUpdateRequest request, CancellationToken ct = default){ Calls.Add($"update:{id}"); return Task.FromResult(new EmergencyAccessDto{ Id = id }); }
        public Task DeleteAsync(string id, CancellationToken ct = default){ Calls.Add($"delete:{id}"); return Task.CompletedTask; }
        public Task<EmergencyAccessDto> InitiateAsync(string id, CancellationToken ct = default){ Calls.Add($"initiate:{id}"); return Task.FromResult(new EmergencyAccessDto{ Id = id, Status = 3 }); }
        public Task<EmergencyAccessDto> ApproveAsync(string id, CancellationToken ct = default){ Calls.Add($"approve:{id}"); return Task.FromResult(new EmergencyAccessDto{ Id = id, Status = 4 }); }
        public Task<EmergencyAccessDto> RejectAsync(string id, CancellationToken ct = default){ Calls.Add($"reject:{id}"); return Task.FromResult(new EmergencyAccessDto{ Id = id, Status = 2 }); }
        public Task<EmergencyAccessViewResponse> ViewAsync(string id, CancellationToken ct = default){ Calls.Add($"view:{id}"); return Task.FromResult(ViewResult); }
        public Task<EmergencyAccessTakeoverResponse> TakeoverAsync(string id, CancellationToken ct = default){ Calls.Add($"takeover:{id}"); return Task.FromResult(TakeoverResult); }
        public Task PasswordAsync(string id, EmergencyAccessPasswordRequest request, CancellationToken ct = default){ Calls.Add($"password:{id}"); LastPassword = request; return Task.CompletedTask; }
        public Task DeleteAccountAsync(DeleteAccountRequest request, CancellationToken ct = default){ Calls.Add("delacct"); return Task.CompletedTask; }
    }

    private static VaultSession UnlockedSession(out SymmetricCryptoKey userKey)
    {
        userKey = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var s = new VaultSession();
        s.SetUnlockedKey(userKey);
        return s;
    }

    [Fact]
    public async Task GetTrusted_MapsDtoToModel()
    {
        var api = new FakeEaApi {
            Trusted = new ListResponse<EmergencyAccessGranteeDetailsDto>(
                new[] { new EmergencyAccessGranteeDetailsDto { Id="e1", Status=2, Type=1, WaitTimeDays=7, GranteeId="u2", Email="b@x.com", Name="B" } },
                null, null) };
        var svc = new EmergencyAccessService(api, new CryptoService(), new VaultDecryptor(new CryptoService()), new VaultSession());
        var list = await svc.GetTrustedAsync(TestContext.Current.CancellationToken);
        var c = Assert.Single(list);
        Assert.Equal("e1", c.Id);
        Assert.Equal(EmergencyAccessStatus.Confirmed, c.Status);
        Assert.Equal(EmergencyAccessType.Takeover, c.Type);
        Assert.Equal("b@x.com", c.Email);
    }

    [Fact]
    public async Task Invite_SendsEmailTypeWait()
    {
        var api = new FakeEaApi();
        var svc = new EmergencyAccessService(api, new CryptoService(), new VaultDecryptor(new CryptoService()), new VaultSession());
        await svc.InviteAsync("b@x.com", EmergencyAccessType.Takeover, 7, TestContext.Current.CancellationToken);
        Assert.Equal("b@x.com", api.LastInvite!.Email);
        Assert.Equal(1, api.LastInvite.Type);
        Assert.Equal(7, api.LastInvite.WaitTimeDays);
    }

    [Fact]
    public async Task Confirm_EncryptsUserKeyToGranteePublicKey()
    {
        // 生成一对 RSA 密钥充当受托方；用公钥让 service 加密，再用私钥还原校验
        using var rsa = RSA.Create(2048);
        var pubDer = rsa.ExportSubjectPublicKeyInfo();
        var api = new FakeEaApi { PublicKeyB64 = Convert.ToBase64String(pubDer) };
        var crypto = new CryptoService();
        var session = UnlockedSession(out var userKey);
        var svc = new EmergencyAccessService(api, crypto, new VaultDecryptor(crypto), session);

        await svc.ConfirmAsync("e1", "u2", TestContext.Current.CancellationToken);

        Assert.Contains("pubkey:u2", api.Calls);
        Assert.Contains("confirm:e1", api.Calls);
        // 还原 service 发出的 key，应等于 userKey.FullKey
        var enc = Crypto.EncString.Parse(api.LastConfirm!.Key);
        var recovered = crypto.DecryptRsa(enc, rsa.ExportPkcs8PrivateKey());
        Assert.Equal(userKey.FullKey, recovered);
    }

    [Fact]
    public async Task Confirm_WhenLocked_Throws()
    {
        var api = new FakeEaApi { PublicKeyB64 = "x" };
        var svc = new EmergencyAccessService(api, new CryptoService(), new VaultDecryptor(new CryptoService()), new VaultSession());
        await Assert.ThrowsAnyAsync<Exception>(() => svc.ConfirmAsync("e1", "u2", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Approve_Reject_Remove_CallApi()
    {
        var api = new FakeEaApi();
        var svc = new EmergencyAccessService(api, new CryptoService(), new VaultDecryptor(new CryptoService()), new VaultSession());
        await svc.ApproveAsync("e1", TestContext.Current.CancellationToken);
        await svc.RejectAsync("e2", TestContext.Current.CancellationToken);
        await svc.RemoveAsync("e3", TestContext.Current.CancellationToken);
        Assert.Contains("approve:e1", api.Calls);
        Assert.Contains("reject:e2", api.Calls);
        Assert.Contains("delete:e3", api.Calls);
    }

    [Fact]
    public async Task View_DecryptsGrantorCiphers()
    {
        var crypto = new CryptoService();
        var decryptor = new VaultDecryptor(crypto);

        // 受托方自己的 RSA 私钥（PKCS8），其 EncryptedPrivateKey = Encrypt(pkcs8, myUserKey)
        using var rsa = RSA.Create(2048);
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var session = UnlockedSession(out var myUserKey);
        session.SetEncryptedPrivateKey(crypto.Encrypt(pkcs8, myUserKey).ToString());

        // 授予方 userKey + 用它加密的一条 Login cipher（构造 CipherDto）
        var grantorKey = new SymmetricCryptoKey(Enumerable.Range(1, 64).Select(i => (byte)i).ToArray());
        var nameEnc = crypto.Encrypt(System.Text.Encoding.UTF8.GetBytes("Secret Item"), grantorKey).ToString();
        var cipherDto = new CipherDto(
            Id: "c1",
            Type: 1,
            Name: nameEnc,
            Notes: null,
            Key: null,
            OrganizationId: null,
            FolderId: null,
            Favorite: false,
            Reprompt: 0,
            Login: new LoginDto(null, null, null, null),
            Card: null,
            Identity: null,
            SecureNote: null,
            SshKey: null,
            Fields: null,
            CreationDate: null,
            RevisionDate: null,
            DeletedDate: null);

        // keyEncrypted = RSA(grantorKey.FullKey → 受托方公钥)
        var keyEnc = crypto.EncryptRsa(grantorKey.FullKey, rsa.ExportSubjectPublicKeyInfo()).ToString();

        var api = new FakeEaApi();
        api.ViewResult = new EmergencyAccessViewResponse { Ciphers = new[] { cipherDto }, KeyEncrypted = keyEnc };
        var svc = new EmergencyAccessService(api, crypto, decryptor, session);

        var recovered = await svc.ViewAsync("e1", "a@x.com", TestContext.Current.CancellationToken);

        Assert.Equal("a@x.com", recovered.GrantorEmail);
        var item = Assert.Single(recovered.Ciphers);
        Assert.Equal("Secret Item", item.Name);
    }

    [Fact]
    public async Task Takeover_DerivesNewMasterPasswordAndRewrapsKey()
    {
        var crypto = new CryptoService();
        using var rsa = RSA.Create(2048);
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var session = UnlockedSession(out var myUserKey);
        session.SetEncryptedPrivateKey(crypto.Encrypt(pkcs8, myUserKey).ToString());

        var grantorKey = new SymmetricCryptoKey(Enumerable.Range(2, 64).Select(i => (byte)i).ToArray());
        var keyEnc = crypto.EncryptRsa(grantorKey.FullKey, rsa.ExportSubjectPublicKeyInfo()).ToString();

        var api = new FakeEaApi();
        api.TakeoverResult = new EmergencyAccessTakeoverResponse { Kdf = 0, KdfIterations = 600000, KeyEncrypted = keyEnc };
        var svc = new EmergencyAccessService(api, crypto, new VaultDecryptor(crypto), session);

        await svc.TakeoverAndResetPasswordAsync("e1", "a@x.com", "New-Pass-1!", TestContext.Current.CancellationToken);

        Assert.NotNull(api.LastPassword);
        // 用新密码+授予方邮箱+KDF 还原 newMasterKey，解开 key 应得回 grantorKey.FullKey
        var newMasterKey = crypto.DeriveMasterKey("New-Pass-1!", "a@x.com", Core.Enums.KdfType.Pbkdf2, 600000, null, null);
        var stretched = crypto.StretchMasterKey(newMasterKey);
        var unwrapped = crypto.DecryptUserKey(stretched, Crypto.EncString.Parse(api.LastPassword!.Key));
        Assert.Equal(grantorKey.FullKey, unwrapped.FullKey);
        Assert.False(string.IsNullOrEmpty(api.LastPassword.NewMasterPasswordHash));
    }
}
