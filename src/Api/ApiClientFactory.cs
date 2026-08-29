using Core.Models;
using Core.Services;
using Core.Session;

namespace Api;

public sealed class ApiClientFactory : IApiClientFactory
{
    private readonly Func<bool, HttpMessageHandler> _transportFactory;
    private readonly TimeSpan _transferIdleTimeout;

    public ApiClientFactory()
        : this(CreateDefaultTransport, RequestDeadlineHandler.DefaultStreamingIdleTimeout)
    {
    }

    public ApiClientFactory(ITransferIdleTimeoutPolicy timeoutPolicy)
        : this(
            CreateDefaultTransport,
            (timeoutPolicy ?? throw new ArgumentNullException(nameof(timeoutPolicy))).IdleTimeout)
    {
    }

    public ApiClientFactory(Func<bool, HttpMessageHandler> transportFactory)
        : this(transportFactory, RequestDeadlineHandler.DefaultStreamingIdleTimeout)
    {
    }

    public ApiClientFactory(
        Func<bool, HttpMessageHandler> transportFactory,
        TimeSpan transferIdleTimeout)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        if (transferIdleTimeout != Timeout.InfiniteTimeSpan && transferIdleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(transferIdleTimeout));
        _transferIdleTimeout = transferIdleTimeout;
    }

    public TClient CreateAnonymous<TClient>(ServerEndpoints endpoints)
        where TClient : class, IAnonymousApiRole
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var headers = new ClientHeadersHandler
        {
            InnerHandler = _transportFactory(false),
        };
        var deadline = new RequestDeadlineHandler(
            streamingIdleTimeout: _transferIdleTimeout)
        {
            InnerHandler = headers,
        };
        return Cast<TClient>(
            new ApiClient(CreateHttpClient(deadline), endpoints));
    }

    public TClient CreateAnonymous<TClient>(ServerOrigin origin)
        where TClient : class, IAnonymousApiRole
        => CreateAnonymous<TClient>(ServerEndpoints.ForSelfHosted(origin.Value));

    public IAuthenticatedApiClient CreateAuthenticated(
        ServerEndpoints endpoints,
        Func<AccessTokenBinding?> tokenProvider,
        Func<AccessTokenBinding, CancellationToken, Task<bool>> refreshAsync)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var auth = new AuthHeaderHandler(endpoints, tokenProvider, refreshAsync)
        {
            InnerHandler = _transportFactory(true),
        };
        var deadline = new RequestDeadlineHandler(
            streamingIdleTimeout: _transferIdleTimeout)
        {
            InnerHandler = auth,
        };
        return new ApiClient(CreateHttpClient(deadline), endpoints);
    }

    public IAuthenticatedApiClient CreateAuthenticated(
        ServerOrigin origin,
        Func<AccessTokenBinding?> tokenProvider,
        Func<CancellationToken, Task<bool>> refreshAsync)
        => CreateAuthenticated(
            ServerEndpoints.ForSelfHosted(origin.Value),
            tokenProvider,
            (_, ct) => refreshAsync(ct));

    private static TClient Cast<TClient>(ApiClient client)
        where TClient : class =>
        client as TClient
        ?? throw new NotSupportedException(
            $"Unsupported API role: {typeof(TClient).FullName}");

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler, disposeHandler: true)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static HttpMessageHandler CreateDefaultTransport(bool authenticated) =>
        new HttpClientHandler
        {
            // Authentication and registration requests carry replayable secrets. Redirects are
            // surfaced to the caller instead of allowing HttpClientHandler to replay them elsewhere.
            AllowAutoRedirect = false,
        };
}
