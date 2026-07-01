using System.Net;
using Api;
using Api.Dtos;
using Xunit;

namespace Vault.Tests;

public class ImportApiClientTests
{
    private static ApiClient NewClient(FakeHttpMessageHandler handler)
    {
        var client = new ApiClient(new HttpClient(handler));
        client.SetBaseAddress("https://vault.example");
        return client;
    }

    [Fact]
    public async Task Import_PostsToImport_WithCiphersFoldersRelations()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);
        var req = new ImportRequest
        {
            Ciphers = new[] { new CipherRequest { Type = 1, Name = "2.n" } },
            Folders = new[] { new FolderRequest { Name = "2.f" } },
            FolderRelationships = new[] { new ImportRelationship { Key = 0, Value = 0 } },
        };
        await client.ImportCiphersAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/ciphers/import", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"folderRelationships\":[{\"key\":0,\"value\":0}]", handler.Bodies[0]);
        Assert.Contains("\"ciphers\":[", handler.Bodies[0]);
        Assert.Contains("\"folders\":[", handler.Bodies[0]);
    }
}
