using System.Net;
using Api;
using Core.Models;
using Api.Dtos;
using Xunit;

namespace Vault.Tests;

public class ApiWriteClientTests
{
    private static ApiClient NewClient(FakeHttpMessageHandler handler)
    {
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));
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

    private static RegisterFinishRequest SampleRegisterFinish() => new(
        "test@example.com",
        "verification-token",
        "master-password-hash",
        "hint",
        "2.wrapped-user-key",
        0,
        600_000,
        null,
        null,
        new RegisterKeys("4.encrypted-private-key", "public-key"));

    private static RegisterRequest SampleRegister() => new(
        "test@example.com",
        "Test User",
        "master-password-hash",
        "hint",
        "2.wrapped-user-key",
        0,
        600_000,
        null,
        null,
        new RegisterKeys("4.encrypted-private-key", "public-key"));

    [Fact]
    public async Task RegistrationVerification_PostsToIdentityAndReadsPlainTextToken()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "verification-token",
                System.Text.Encoding.UTF8,
                "text/plain"),
        });
        var client = NewClient(handler);

        var result = await client.RequestRegistrationVerificationTokenAsync(
            new RegisterVerificationRequest("test@example.com", "Test User"),
            TestContext.Current.CancellationToken);

        var token = Assert.IsType<RegistrationVerificationResult.InlineToken>(result);
        Assert.Equal("verification-token", token.Token);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(
            "/identity/accounts/register/send-verification-email",
            handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"email\":\"test@example.com\"", handler.Bodies[0]);
        Assert.Contains("\"receiveMarketingEmails\":false", handler.Bodies[0]);
    }

    [Fact]
    public async Task RegistrationVerification_NoContent_ReturnsEmailVerificationSent()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = NewClient(handler);

        var result = await client.RequestRegistrationVerificationTokenAsync(
            new RegisterVerificationRequest("test@example.com", null),
            TestContext.Current.CancellationToken);

        Assert.IsType<RegistrationVerificationResult.EmailVerificationSent>(result);
    }

    [Fact]
    public async Task RegistrationVerification_JsonStringToken_IsDecoded()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            "\"verification-token\""));
        var client = NewClient(handler);

        var result = await client.RequestRegistrationVerificationTokenAsync(
            new RegisterVerificationRequest("test@example.com", null),
            TestContext.Current.CancellationToken);

        var token = Assert.IsType<RegistrationVerificationResult.InlineToken>(result);
        Assert.Equal("verification-token", token.Token);
    }

    [Fact]
    public async Task RegistrationVerification_PlainTextToken_IsTrimmed()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "  verification-token\r\n",
                System.Text.Encoding.UTF8,
                "text/plain"),
        });
        var client = NewClient(handler);

        var result = await client.RequestRegistrationVerificationTokenAsync(
            new RegisterVerificationRequest("test@example.com", null),
            TestContext.Current.CancellationToken);

        var token = Assert.IsType<RegistrationVerificationResult.InlineToken>(result);
        Assert.Equal("verification-token", token.Token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    public async Task RegistrationVerification_EmptyPlainText_IsRejected(string body)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                body,
                System.Text.Encoding.UTF8,
                "text/plain"),
        });
        var client = NewClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.RequestRegistrationVerificationTokenAsync(
                new RegisterVerificationRequest("test@example.com", null),
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"   \"")]
    public async Task RegistrationVerification_InvalidJsonToken_IsRejected(string body)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, body));
        var client = NewClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.RequestRegistrationVerificationTokenAsync(
                new RegisterVerificationRequest("test@example.com", null),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegistrationVerification_OversizedToken_IsRejected()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                new string('x', (16 * 1024) + 1),
                System.Text.Encoding.UTF8,
                "text/plain"),
        });
        var client = NewClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.RequestRegistrationVerificationTokenAsync(
                new RegisterVerificationRequest("test@example.com", null),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegistrationVerification_UnexpectedSuccessStatus_IsRejected()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent(
                "verification-token",
                System.Text.Encoding.UTF8,
                "text/plain"),
        });
        var client = NewClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.RequestRegistrationVerificationTokenAsync(
                new RegisterVerificationRequest("test@example.com", null),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegistrationVerification_UnexpectedContentType_IsRejected()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<html>verification-token</html>",
                System.Text.Encoding.UTF8,
                "text/html"),
        });
        var client = NewClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.RequestRegistrationVerificationTokenAsync(
                new RegisterVerificationRequest("test@example.com", null),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegistrationFinish_PostsOfficialLegacyEncryptionFields()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        var result = await client.FinishRegistrationAsync(
            SampleRegisterFinish(),
            TestContext.Current.CancellationToken);

        Assert.IsType<RegistrationWriteResult.Completed>(result);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(
            "/identity/accounts/register/finish",
            handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"emailVerificationToken\":\"verification-token\"", handler.Bodies[0]);
        Assert.Contains("\"userSymmetricKey\":\"2.wrapped-user-key\"", handler.Bodies[0]);
        Assert.Contains("\"userAsymmetricKeys\":{", handler.Bodies[0]);
        Assert.DoesNotContain("\"key\":", handler.Bodies[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RegistrationWrite_UnexpectedSuccessStatus_IsOutcomeUncertain(
        bool finishFlow)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = NewClient(handler);

        var result = finishFlow
            ? await client.FinishRegistrationAsync(
                SampleRegisterFinish(),
                TestContext.Current.CancellationToken)
            : await client.RegisterAsync(
                SampleRegister(),
                TestContext.Current.CancellationToken);

        Assert.IsType<RegistrationWriteResult.OutcomeUncertain>(result);
    }

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
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        // 派生自 HttpRequestException:既有异常处理器行为不变。
        Assert.IsAssignableFrom<HttpRequestException>(ex);
    }

    [Fact]
    public async Task WriteError_400WithValidationErrors_PopulatesStatusAndValidationErrors()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"message":"The model state is invalid.","validationErrors":{"Name":["Name is required.","Name too long."]},"object":"error"}"""));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<VaultWriteException>(() =>
            client.CreateCipherAsync(SampleCipher(), TestContext.Current.CancellationToken));

        Assert.Equal("The model state is invalid.", ex.Message);
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.NotNull(ex.ValidationErrors);
        Assert.Equal(["Name is required.", "Name too long."], ex.ValidationErrors!["Name"]);
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

    [Fact]
    public async Task BulkSoftDelete_PutsToCiphersDelete_WithIdsBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        await client.BulkSoftDeleteCiphersAsync(new[] { "a", "b" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/ciphers/delete", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"ids\":[\"a\",\"b\"]", handler.Bodies[0]);
    }

    [Fact]
    public async Task BulkHardDelete_PostsToCiphersDelete_WithIdsBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        await client.BulkHardDeleteCiphersAsync(new[] { "a" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/ciphers/delete", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"ids\":[\"a\"]", handler.Bodies[0]);
    }

    [Fact]
    public async Task BulkRestore_PutsToCiphersRestore_WithIdsBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        await client.BulkRestoreCiphersAsync(new[] { "a", "b" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/ciphers/restore", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"ids\":[\"a\",\"b\"]", handler.Bodies[0]);
    }

    [Fact]
    public async Task BulkMove_PutsToCiphersMove_WithIdsAndFolderId()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        await client.BulkMoveCiphersAsync(new[] { "a" }, "f1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/ciphers/move", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"ids\":[\"a\"]", handler.Bodies[0]);
        Assert.Contains("\"folderId\":\"f1\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task BulkMove_NullFolder_SerializesFolderIdNull()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        await client.BulkMoveCiphersAsync(new[] { "a" }, null, TestContext.Current.CancellationToken);

        Assert.Contains("\"folderId\":null", handler.Bodies[0]);
    }
}
