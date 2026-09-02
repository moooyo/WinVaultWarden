using System.Net;
using Api;
using Core.Models;
using Api.Dtos;
using Xunit;

namespace Vault.Tests;

public class EmergencyAccessApiClientTests
{
    private static ApiClient NewClient(FakeHttpMessageHandler handler)
    {
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));
        return client;
    }

    // ===== GET: list trusted (GET returns JSON) =====

    [Fact]
    public async Task GetTrusted_GetsEmergencyAccessTrusted()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            "{\"data\":[],\"continuationToken\":null,\"object\":\"list\"}"));
        var client = NewClient(handler);
        var res = await ((IEmergencyAccessApiClient)client).GetTrustedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api/emergency-access/trusted", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.NotNull(res);
    }

    // ===== GET: list granted (GET returns JSON) =====

    [Fact]
    public async Task GetGranted_GetsEmergencyAccessGranted()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            "{\"data\":[],\"continuationToken\":null,\"object\":\"list\"}"));
        var client = NewClient(handler);
        var res = await ((IEmergencyAccessApiClient)client).GetGrantedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api/emergency-access/granted", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.NotNull(res);
    }

    // ===== GET: public key =====

    [Fact]
    public async Task GetPublicKey_GetsUsersPublicKey()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            "{\"userId\":\"u2\",\"publicKey\":\"BASE64\"}"));
        var client = NewClient(handler);
        var res = await ((IEmergencyAccessApiClient)client).GetPublicKeyAsync("u2", TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api/users/u2/public-key", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("BASE64", res.PublicKey);
    }

    // ===== POST with body, no return =====

    [Fact]
    public async Task Invite_PostsToInvite_WithBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);
        await ((IEmergencyAccessApiClient)client).InviteAsync(
            new EmergencyAccessInviteRequest { Email = "b@x.com", Type = 1, WaitTimeDays = 7 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/emergency-access/invite", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"email\":\"b@x.com\"", handler.Bodies[0]);
    }

    // ===== POST no body, no return (reinvite) =====

    [Fact]
    public async Task Reinvite_PostsToReinvite_NoBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);
        await ((IEmergencyAccessApiClient)client).ReinviteAsync("e1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/emergency-access/e1/reinvite", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(string.Empty, handler.Bodies[0]);
    }

    // ===== POST with body, no return (accept) =====

    [Fact]
    public async Task Accept_PostsToAccept_WithBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);
        await ((IEmergencyAccessApiClient)client).AcceptAsync("e1",
            new EmergencyAccessAcceptRequest { Token = "tok123" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/emergency-access/e1/accept", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"token\":\"tok123\"", handler.Bodies[0]);
    }

    // ===== POST with body, empty success response (confirm) =====

    [Fact]
    public async Task Confirm_PostsKey_AcceptsVaultwardenJsonSuccessResponse()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            "{\"id\":\"e1\",\"status\":2,\"type\":1,\"waitTimeDays\":7}"));
        var client = NewClient(handler);
        await ((IEmergencyAccessApiClient)client).ConfirmAsync("e1",
            new EmergencyAccessConfirmRequest { Key = "4.enc" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/emergency-access/e1/confirm", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"key\":\"4.enc\"", handler.Bodies[0]);
    }

    // ===== PUT with body, empty success response (update) =====

    [Fact]
    public async Task Update_PutsToId_AcceptsEmptySuccessResponse()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = NewClient(handler);
        await ((IEmergencyAccessApiClient)client).UpdateAsync("e1",
            new EmergencyAccessUpdateRequest { Type = 0, WaitTimeDays = 14 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/emergency-access/e1", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"waitTimeDays\":14", handler.Bodies[0]);
    }

    // ===== DELETE no body =====

    [Fact]
    public async Task Delete_DeletesById_NoBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);
        await ((IEmergencyAccessApiClient)client).DeleteAsync("e1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Equal("/api/emergency-access/e1", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(string.Empty, handler.Bodies[0]);
    }

    // ===== POST no body, empty success response (initiate) =====

    [Fact]
    public async Task Initiate_PostsNoBody_AcceptsEmptySuccessResponse()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = NewClient(handler);
        await ((IEmergencyAccessApiClient)client).InitiateAsync("e1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/emergency-access/e1/initiate", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(string.Empty, handler.Bodies[0]);
    }

    // ===== POST no body, empty success response (approve) =====

    [Fact]
    public async Task Approve_PostsNoBody_AcceptsEmptySuccessResponse()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = NewClient(handler);
        await ((IEmergencyAccessApiClient)client).ApproveAsync("e2", TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/emergency-access/e2/approve", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(string.Empty, handler.Bodies[0]);
    }

    // ===== POST no body, empty success response (reject) =====

    [Fact]
    public async Task Reject_PostsNoBody_AcceptsEmptySuccessResponse()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = NewClient(handler);
        await ((IEmergencyAccessApiClient)client).RejectAsync("e3", TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/emergency-access/e3/reject", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(string.Empty, handler.Bodies[0]);
    }

    // ===== POST no body, returns JSON (view) =====

    [Fact]
    public async Task View_PostsNoBody_ReturnsCiphersAndKey()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            "{\"ciphers\":[],\"keyEncrypted\":\"4.k\"}"));
        var client = NewClient(handler);
        var res = await ((IEmergencyAccessApiClient)client).ViewAsync("e1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/emergency-access/e1/view", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("4.k", res.KeyEncrypted);
    }

    // ===== POST no body, returns JSON (takeover) =====

    [Fact]
    public async Task Takeover_PostsNoBody_ReturnsTakeoverResponse()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            "{\"kdf\":0,\"kdfIterations\":600000,\"salt\":\"custom-salt\",\"keyEncrypted\":\"4.k\"}"));
        var client = NewClient(handler);
        var res = await ((IEmergencyAccessApiClient)client).TakeoverAsync("e1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/emergency-access/e1/takeover", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(string.Empty, handler.Bodies[0]);
        Assert.Equal(600000, res.KdfIterations);
        Assert.Equal("custom-salt", res.Salt);
    }

    // ===== POST with body, no return (password) =====

    [Fact]
    public async Task Password_PostsPasswordBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);
        await ((IEmergencyAccessApiClient)client).PasswordAsync("e1",
            new EmergencyAccessPasswordRequest { NewMasterPasswordHash = "hash", Key = "4.k" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/emergency-access/e1/password", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"newMasterPasswordHash\":\"hash\"", handler.Bodies[0]);
    }

}
