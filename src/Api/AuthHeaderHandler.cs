using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Core.Models;
using Core.Session;

[assembly: InternalsVisibleTo("Api.Tests")]

namespace Api;

public sealed class AuthHeaderHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<bool> DisableAutomaticRetryKey =
        new("WinVaultWarden.DisableAutomaticRetry");

    public static readonly HttpRequestOptionsKey<bool> SuppressBearerAuthorizationKey =
        new("WinVaultWarden.SuppressBearerAuthorization");

    private static readonly HttpRequestOptionsKey<AuthorizationCapture> AuthorizationCaptureKey =
        new("WinVaultWarden.AuthorizationCapture");

    private readonly ServerEndpoint _apiAudience;
    private readonly ServerEndpoint _identityEndpoint;
    private readonly Func<AccessTokenBinding?> _tokenProvider;
    private readonly Func<AccessTokenBinding, CancellationToken, Task<bool>> _refreshAsync;

    public AuthHeaderHandler(
        ServerEndpoints endpoints,
        Func<AccessTokenBinding?> tokenProvider,
        Func<AccessTokenBinding, CancellationToken, Task<bool>> refreshAsync)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        _apiAudience = endpoints.Api;
        _identityEndpoint = endpoints.Identity;
        _tokenProvider = tokenProvider;
        _refreshAsync = refreshAsync;
    }

    public AuthHeaderHandler(
        ServerOrigin clientOrigin,
        Func<AccessTokenBinding?> tokenProvider,
        Func<CancellationToken, Task<bool>> refreshAsync)
        : this(
            ServerEndpoints.ForSelfHosted(clientOrigin.Value),
            tokenProvider,
            (_, ct) => refreshAsync(ct))
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var attached = ApplyHeaders(request);
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized
            || attached is null
            || IsTokenEndpoint(request)
            || request.Options.TryGetValue(DisableAutomaticRetryKey, out var disabled)
               && disabled)
            return response;

        try
        {
            using var retry = await CloneRequestAsync(request, ct).ConfigureAwait(false);
            if (!await _refreshAsync(attached, ct).ConfigureAwait(false))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    RequestMessage = request,
                };
            }

            if (ApplyHeaders(retry) is null)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    RequestMessage = request,
                };
            }
            return await base.SendAsync(retry, ct).ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }
    }

    internal static AuthorizationCapture CaptureAuthorization(HttpRequestMessage request)
    {
        var capture = new AuthorizationCapture();
        request.Options.Set(AuthorizationCaptureKey, capture);
        return capture;
    }

    private AccessTokenBinding? ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.Authorization = null;
        ClientHeadersHandler.Apply(request);
        if (request.Options.TryGetValue(SuppressBearerAuthorizationKey, out var suppressed)
            && suppressed)
        {
            return null;
        }

        var binding = _tokenProvider();
        if (request.RequestUri is not { IsAbsoluteUri: true } target
            || binding is null
            || binding.ApiAudience != _apiAudience
            || !_apiAudience.Matches(target))
            return null;

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", binding.Token);
        if (request.Options.TryGetValue(AuthorizationCaptureKey, out var capture))
            capture.Authorization = binding;
        return binding;
    }

    private bool IsTokenEndpoint(HttpRequestMessage request) =>
        request.RequestUri is { IsAbsoluteUri: true } uri
        && _identityEndpoint.Matches(uri)
        && string.Equals(
            uri.AbsolutePath.TrimEnd('/'),
            _identityEndpoint.Resolve("connect/token").AbsolutePath.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    internal static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        try
        {
            foreach (var header in request.Headers)
            {
                if (!string.Equals(
                        header.Key,
                        "Authorization",
                        StringComparison.OrdinalIgnoreCase))
                    clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Options.TryGetValue(AuthorizationCaptureKey, out var capture))
                clone.Options.Set(AuthorizationCaptureKey, capture);
            if (request.Options.TryGetValue(DisableAutomaticRetryKey, out var disableRetry))
                clone.Options.Set(DisableAutomaticRetryKey, disableRetry);
            if (request.Options.TryGetValue(SuppressBearerAuthorizationKey, out var suppressBearer))
                clone.Options.Set(SuppressBearerAuthorizationKey, suppressBearer);
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                clone.Content = new ZeroingByteArrayContent(bytes);
                foreach (var header in request.Content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return clone;
        }
        catch
        {
            clone.Dispose();
            throw;
        }
    }

    internal sealed class AuthorizationCapture
    {
        public AccessTokenBinding? Authorization { get; set; }
    }

    internal sealed class ZeroingByteArrayContent(byte[] buffer) : ByteArrayContent(buffer)
    {
        private readonly byte[] _buffer = buffer;
        private int _disposed;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        internal bool IsCleared
        {
            get
            {
                foreach (var value in _buffer)
                {
                    if (value != 0)
                        return false;
                }
                return true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CryptographicOperations.ZeroMemory(_buffer);
                Volatile.Write(ref _disposed, 1);
            }
            base.Dispose(disposing);
        }
    }
}
