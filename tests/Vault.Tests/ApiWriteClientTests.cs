using System.Net;
using Api;
using Api.Dtos;
using Xunit;

namespace Vault.Tests;

public class ApiWriteClientTests
{
    private static ApiClient NewClient(FakeHttpMessageHandler handler)
    {
        var client = new ApiClient(new HttpClient(handler));
        client.SetBaseAddress("https://vault.example");
        return client;
    }

    private static CipherRequest SampleCipher() => new()
    {
        Type = 1,
        Name = "2.name",
        Favorite = true,
        Reprompt = 0,
        Key = "2.wrapped",
        Login = new LoginRequest { Username = "2.user" },
    };

    [Fact]
    public async Task CreateCipher_PostsToCiphersWithCamelCaseBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        await client.CreateCipherAsync(SampleCipher(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/ciphers", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"type\":1", handler.Bodies[0]);
        Assert.Contains("\"name\":\"2.name\"", handler.Bodies[0]);
        Assert.Contains("\"login\":{", handler.Bodies[0]);
        Assert.Contains("\"username\":\"2.user\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task UpdateCipher_PutsToCipherId()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        await client.UpdateCipherAsync("c-1", SampleCipher(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/ciphers/c-1", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"name\":\"2.name\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task SoftDeleteCipher_PutsToDeleteSubpathWithoutBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        await client.SoftDeleteCipherAsync("c-1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/ciphers/c-1/delete", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(string.Empty, handler.Bodies[0]);
    }

    [Fact]
    public async Task HardDeleteCipher_DeletesCipherId()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        await client.HardDeleteCipherAsync("c-1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Equal("/api/ciphers/c-1", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RestoreCipher_PutsToRestoreSubpath()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        await client.RestoreCipherAsync("c-1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/ciphers/c-1/restore", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task CreateFolder_PostsToFolders()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        await client.CreateFolderAsync(new FolderRequest { Name = "2.folder" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/folders", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"name\":\"2.folder\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task UpdateFolder_PutsToFolderId()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        await client.UpdateFolderAsync("f-1", new FolderRequest { Name = "2.folder" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/folders/f-1", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"name\":\"2.folder\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task DeleteFolder_DeletesFolderId()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        await client.DeleteFolderAsync("f-1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Equal("/api/folders/f-1", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task WriteError_400WithMessage_ThrowsVaultWriteException()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"message":"The client copy of this cipher is out of date. Resync the client and try again.","validationErrors":{},"object":"error"}"""));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<VaultWriteException>(() =>
            client.UpdateCipherAsync("c-1", SampleCipher(), TestContext.Current.CancellationToken));

        Assert.Equal("The client copy of this cipher is out of date. Resync the client and try again.", ex.Message);
    }

    [Fact]
    public async Task WriteError_400EmptyBody_ThrowsWithReasonPhrase()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<VaultWriteException>(() =>
            client.SoftDeleteCipherAsync("c-1", TestContext.Current.CancellationToken));

        Assert.Equal("Bad Request", ex.Message);
    }
}
