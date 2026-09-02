using System.Net;
using Api;
using Core.Models;
using Xunit;

namespace Api.Tests;

public sealed class AccountRevisionWireTests
{
    [Fact]
    public async Task GetAccountRevisionDate_UsesAuthenticatedApiRouteAndReadsEpochMilliseconds()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "1788134400123",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
        using var client = new ApiClient(
            new HttpClient(handler),
            ServerEndpoints.ForSelfHosted("https://vault.example/warden"));

        var revision = await client.GetAccountRevisionDateAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(1_788_134_400_123L, revision);
        Assert.Equal(
            "/warden/api/accounts/revision-date",
            handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Request.Method);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(response);
        }
    }
}
