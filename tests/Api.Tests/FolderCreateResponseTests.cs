using System.Net;
using System.Text;
using System.Text.Json;
using Api.Dtos;
using Core.Models;
using Xunit;

namespace Api.Tests;

public sealed class FolderCreateResponseTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    public async Task CreateFolderWithResponse_ReturnsServerIdentityAndPostsEncryptedName(
        HttpStatusCode statusCode)
    {
        var handler = new RecordingHandler(statusCode,
            """
            {
              "id": "server-folder-id",
              "name": "2.encrypted-folder-name",
              "revisionDate": "2026-11-10T00:00:00Z",
              "object": "folder"
            }
            """);
        using var client = CreateClient(handler);

        var folder = await client.CreateFolderWithResponseAsync(
            new FolderRequest { Name = "2.encrypted-folder-name" },
            TestContext.Current.CancellationToken);

        Assert.Equal("server-folder-id", folder.Id);
        Assert.Equal("2.encrypted-folder-name", folder.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-11-10T00:00:00Z"), folder.RevisionDate);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/warden/api/folders", handler.Uri!.AbsolutePath);
        Assert.Equal(string.Empty, handler.Uri.Query);
        using var document = JsonDocument.Parse(handler.Body!);
        var property = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("name", property.Name);
        Assert.Equal("2.encrypted-folder-name", property.Value.GetString());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"id\":null}")]
    [InlineData("{\"id\":\"\"}")]
    [InlineData("{\"id\":\"   \"}")]
    public async Task CreateFolderWithResponse_UnreadableOrMissingIdentity_PreservesAcceptedStatus(
        string responseBody)
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, responseBody);
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<FolderCreateSuccessResponseReadException>(() =>
            client.CreateFolderWithResponseAsync(
                new FolderRequest { Name = "2.encrypted-folder-name" },
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Created, exception.StatusCode);
        Assert.NotNull(exception.InnerException);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task CreateFolderWithResponse_RejectedRequest_RemainsAWriteFailure(
        HttpStatusCode statusCode)
    {
        var handler = new RecordingHandler(statusCode,
            """
            {
              "message": "Folder name was rejected.",
              "validationErrors": { "name": ["Invalid folder name."] }
            }
            """);
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<VaultWriteException>(() =>
            client.CreateFolderWithResponseAsync(
                new FolderRequest { Name = "2.encrypted-folder-name" },
                TestContext.Current.CancellationToken));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Equal("Folder name was rejected.", exception.ServerMessage);
        Assert.Equal("Invalid folder name.", Assert.Single(exception.ValidationErrors!["name"]));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CreateFolderWithResponse_DefaultImplementation_DoesNotCreateWithoutReturningIdentity()
    {
        var legacyClient = new LegacyWriteApi();
        IVaultWriteApiClient client = legacyClient;

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.CreateFolderWithResponseAsync(
                new FolderRequest { Name = "2.encrypted-folder-name" },
                TestContext.Current.CancellationToken));

        Assert.Equal(0, legacyClient.CreateFolderCalls);
    }

    private static ApiClient CreateClient(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        ServerEndpoints.ForSelfHosted("https://vault.example/warden"));

    private sealed class RecordingHandler(HttpStatusCode statusCode, string responseBody)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Method = request.Method;
            Uri = request.RequestUri;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class LegacyWriteApi : IVaultWriteApiClient
    {
        public int CreateFolderCalls { get; private set; }

        public Task CreateFolderAsync(FolderRequest request, CancellationToken ct = default)
        {
            CreateFolderCalls++;
            return Task.CompletedTask;
        }

        public Task CreateCipherAsync(CipherRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task UpdateCipherAsync(string cipherId, CipherRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SoftDeleteCipherAsync(string cipherId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task HardDeleteCipherAsync(string cipherId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task BulkSoftDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task BulkHardDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task BulkRestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task BulkMoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task UpdateFolderAsync(string folderId, FolderRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ImportCiphersAsync(ImportRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
