using System.Net;
using System.Net.Http.Headers;
using Api;
using Core.Models;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

// Finding 1: 孤儿 Send 补偿删除必须用【操作开始、lease 仍 current 时冻结的 AccessTokenBinding】,
// 而不是 lease 绑定的 token provider —— 后者在 lease 失效(vault Lock)后返回 null,补偿 DELETE 无 Bearer,
// 生产环境必被服务端 401 拒绝、留下孤儿记录。SendCompensationClient 用真实 ApiClientFactory 造一次性认证
// 客户端,只带冻结令牌发一个 DELETE 到冻结 binding 的 origin,绝不触碰会话状态。
public sealed class SendCompensationClientTests
{
    private static (ApiClientFactory factory, FakeHttpMessageHandler handler) NewFactory()
    {
        var handler = new FakeHttpMessageHandler();
        return (new ApiClientFactory(_ => handler), handler);
    }

    // 核心回归:操作开始时从 live 会话冻结 binding,随后 vault 被 Lock(lease 失效),补偿 DELETE 仍必须
    // 携带 Authorization: Bearer <冻结令牌>,且只发到冻结 binding 的原 origin。
    [Fact]
    public async Task DeleteAsync_WithFrozenBinding_AfterSessionLock_SendsBearerToBoundOrigin()
    {
        var (factory, handler) = NewFactory();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));

        var session = new VaultSession();
        var origin = new ServerOrigin("https://a.example");
        var lease = session.BeginAccountSession("account-a", "a@example.com", origin);
        session.TryCommit(lease, () =>
        {
            session.SetUnlockedKey(new SymmetricCryptoKey(
                Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
            session.SetTokens("token-a", "refresh-a");
        });

        // 操作开始、lease 仍 current 时冻结 binding。
        var binding = session.GetAccessTokenBinding(lease);
        Assert.NotNull(binding);

        // vault 被 Lock:lease 失效后 lease 绑定的 provider 会返回 null(旧代码补偿必无 Bearer → 401)。
        session.Lock();
        Assert.Null(session.GetAccessTokenBinding(lease));

        var compensation = new SendCompensationClient(factory);
        await compensation.DeleteAsync(binding!, "orphan-1", TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("a.example", request.RequestUri!.Host);
        Assert.Equal("/api/sends/orphan-1", request.RequestUri.AbsolutePath);
        Assert.Equal(
            new AuthenticationHeaderValue("Bearer", "token-a"),
            request.Headers.Authorization);
    }

    // origin 隔离:每次补偿只发到各自冻结 binding 的 origin,携带各自的令牌;一个 origin 的令牌绝不出现在
    // 另一个 origin 的请求上。
    [Fact]
    public async Task DeleteAsync_FrozenBindingToken_NeverLeaksToForeignOrigin()
    {
        var (factory, handler) = NewFactory();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));

        var bindingA = new AccessTokenBinding(
            "account-a", new ServerOrigin("https://a.example"), 3, "token-a");
        var bindingB = new AccessTokenBinding(
            "account-b", new ServerOrigin("https://b.example"), 7, "token-b");

        var compensation = new SendCompensationClient(factory);
        await compensation.DeleteAsync(bindingA, "a-1", TestContext.Current.CancellationToken);
        await compensation.DeleteAsync(bindingB, "b-1", TestContext.Current.CancellationToken);

        var toA = handler.Requests[0];
        var toB = handler.Requests[1];
        Assert.Equal("a.example", toA.RequestUri!.Host);
        Assert.Equal("token-a", toA.Headers.Authorization!.Parameter);
        Assert.Equal("b.example", toB.RequestUri!.Host);
        Assert.Equal("token-b", toB.Headers.Authorization!.Parameter);
        // 冻结 binding 的令牌绝不跨 origin 泄漏。
        Assert.NotEqual("token-a", toB.Headers.Authorization.Parameter);
    }
}
