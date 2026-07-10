using System.Net;
using System.Text;

namespace Vault.Tests;

public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> Bodies { get; } = new();

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        _responses.Enqueue((request, _) => Task.FromResult(response(request)));

    public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
        _responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));

        return _responses.Count == 0
            ? Json(HttpStatusCode.NotFound, "{}")
            : await _responses.Dequeue()(request, cancellationToken);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
