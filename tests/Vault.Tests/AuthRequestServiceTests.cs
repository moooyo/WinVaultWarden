using System.Security.Cryptography;
using Api.Dtos;
using Core.Abstractions;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

// TDD — 先写测试，再写实现。
// 覆盖四个场景：
//   (a) Approve：发送 encType-4 Key，RSA 解密后 round-trips 回 UserKey.FullKey
//   (b) Approve 时已锁定（无 UserKey）：抛 AuthRequestOperationException，API 未被调用
//   (c) Deny：发送 RequestApproved=false，Key="" ，设备标识符正确
//   (d) ListPending：字段映射正确
public class AuthRequestServiceTests
{
    // ── 测试基础设施 ──────────────────────────────────────────────────────────
    private readonly CryptoService _crypto = new();
    private readonly SymmetricCryptoKey _userKey =
        new(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

    private const string DeviceId = "device-uuid-1234";

    private MemoryTokenStore MakeTokenStore()
    {
        var store = new MemoryTokenStore();
        store.Save(new PersistedSession(
            ServerUrl: "http://10.0.1.20:8080",
            Email: "test@example.com",
            DeviceIdentifier: DeviceId,
            RefreshToken: "rt-token",
            ProtectedUserKey: "2.xxx|yyy|zzz",
            KdfType: Core.Enums.KdfType.Pbkdf2,
            KdfIterations: 600000,
            KdfMemory: null,
            KdfParallelism: null));
        return store;
    }

    private VaultSession MakeUnlockedSession()
    {
        var session = new VaultSession();
        session.SetUnlockedKey(_userKey);
        return session;
    }

    private VaultSession MakeLockedSession() => new VaultSession(); // UserKey == null

    private AuthRequestService MakeService(
        FakeAuthRequestApiClient api,
        VaultSession? session = null,
        ITokenStore? tokenStore = null)
    {
        return new AuthRequestService(
            api,
            _crypto,
            session ?? MakeUnlockedSession(),
            tokenStore ?? MakeTokenStore());
    }

    // ── (a) ApproveAsync：encType-4 密钥可通过对应私钥解密还原 UserKey.FullKey ──────
    [Fact]
    public async Task Approve_SendsEncType4Key_ThatRoundTripsUserKey()
    {
        // 生成一个 RSA 2048 密钥对；SPKI base64 作为 publicKey 入参
        using var rsa = RSA.Create(2048);
        var publicKeySpki = rsa.ExportSubjectPublicKeyInfo();
        var publicKeyBase64 = Convert.ToBase64String(publicKeySpki);
        var privateKeyDer = rsa.ExportPkcs8PrivateKey();

        var api = new FakeAuthRequestApiClient();
        var service = MakeService(api);

        await service.ApproveAsync("req-001", publicKeyBase64, TestContext.Current.CancellationToken);

        // 断言 API 被调用一次 approve
        var single = Assert.Single(api.Calls, c => c == "approve");
        Assert.Equal("req-001", api.LastApproveId);
        Assert.True(api.LastApproveRequest!.RequestApproved);
        Assert.Null(api.LastApproveRequest.MasterPasswordHash);
        Assert.Equal(DeviceId, api.LastApproveRequest.DeviceIdentifier);

        // 解析发送的 Key：必须是 encType=4 的 EncString
        var sentKey = api.LastApproveRequest.Key;
        Assert.StartsWith("4.", sentKey);

        // 用私钥解密后应等于原始 UserKey.FullKey（64 字节）
        var enc = EncString.Parse(sentKey);
        var decrypted = _crypto.DecryptRsa(enc, privateKeyDer);
        Assert.True(decrypted.SequenceEqual(_userKey.FullKey),
            "DecryptRsa(sentKey) 应还原回 UserKey.FullKey");
    }

    // ── (b) Approve 时密码库已锁定 → 抛异常，不调用 API ────────────────────────
    [Fact]
    public async Task Approve_WhenLocked_ThrowsAuthRequestOperationException_AndApiNotCalled()
    {
        var api = new FakeAuthRequestApiClient();
        var service = MakeService(api, session: MakeLockedSession());

        var ex = await Assert.ThrowsAsync<AuthRequestOperationException>(
            () => service.ApproveAsync("req-002", "anyPublicKey", TestContext.Current.CancellationToken));

        Assert.Contains("解锁", ex.Message);
        Assert.Empty(api.Calls);
    }

    // ── (c) DenyAsync：发送 RequestApproved=false，Key 为空字符串 ────────────────
    [Fact]
    public async Task Deny_SendsRequestApprovedFalse_WithEmptyKey()
    {
        var api = new FakeAuthRequestApiClient();
        var service = MakeService(api);

        await service.DenyAsync("req-003", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "approve" }, api.Calls);
        Assert.Equal("req-003", api.LastApproveId);
        Assert.False(api.LastApproveRequest!.RequestApproved);
        Assert.Equal("", api.LastApproveRequest.Key);
        Assert.Equal(DeviceId, api.LastApproveRequest.DeviceIdentifier);
        Assert.Null(api.LastApproveRequest.MasterPasswordHash);
    }

    // ── (d) ListPendingAsync：字段映射正确 ────────────────────────────────────
    [Fact]
    public async Task ListPending_MapsDtoFieldsToModel()
    {
        var resp = new AuthRequestListResponse(
            Data: new List<AuthRequestResponse>
            {
                new(
                    Id: "req-100",
                    PublicKey: "pubKeyBase64==",
                    RequestDeviceType: "Android",
                    RequestIpAddress: "10.0.0.2",
                    Key: null,
                    MasterPasswordHash: null,
                    CreationDate: "2024-06-01T12:00:00Z",
                    ResponseDate: null,
                    RequestApproved: null,
                    Origin: "https://vault.example.com"),
            });

        var api = new FakeAuthRequestApiClient { PendingResult = resp };
        var service = MakeService(api);

        var result = await service.ListPendingAsync(TestContext.Current.CancellationToken);

        var item = Assert.Single(result);
        Assert.Equal("req-100", item.Id);
        Assert.Equal("Android", item.DeviceTypeName);
        Assert.Equal("10.0.0.2", item.IpAddress);
        Assert.Equal("2024-06-01T12:00:00Z", item.CreationDate);
        Assert.Equal("pubKeyBase64==", item.PublicKey);
    }
}
