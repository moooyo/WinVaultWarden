using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Core.Models;
using Core.Services;

namespace App.Services;

public interface IExternalUriLauncher
{
    Task<bool> LaunchAsync(Uri uri, CancellationToken ct = default);
}

public sealed class LoopbackSsoAuthorizationService : ISsoAuthorizationService
{
    public const int FirstCallbackPort = 8065;
    public const int LastCallbackPort = 8070;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private const int RandomValueBytes = 48;
    private const int MaxRequestHeaderBytes = 16 * 1024;
    private const int MaxCallbackAttempts = 16;
    private const string IdentifierMarker = "_identifier=";

    private readonly IExternalUriLauncher _launcher;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _pendingGate = new();
    private Uri? _pendingAuthorizationUri;
    private CancellationToken _pendingAuthorizationToken;

    public LoopbackSsoAuthorizationService(
        IExternalUriLauncher launcher,
        TimeSpan? timeout = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public Task<SsoAuthorizationCode> AuthorizeAsync(
        ServerEndpoints endpoints,
        string email,
        CancellationToken ct = default) => AuthorizeAsync(endpoints, email, null, null, ct);

    public async Task<SsoAuthorizationCode> AuthorizeAsync(
        ServerEndpoints endpoints,
        string email,
        string? organizationIdentifier,
        IProgress<bool>? browserOpened,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var listener = BindListener();

            var verifier = RandomBase64Url();
            var state = RandomBase64Url();
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var redirectUri = $"http://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}";
            var authorizationUri = BuildAuthorizationUri(
                endpoints.WebVault,
                redirectUri,
                state,
                challenge,
                email.Trim(),
                organizationIdentifier);

            bool launched;
            try
            {
                launched = await _launcher.LaunchAsync(authorizationUri, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException("The SSO authorization request timed out.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new SsoAuthorizationException("The system browser could not be opened.", ex);
            }

            if (!launched)
                throw new SsoAuthorizationException("The system browser declined the SSO authorization URI.");

            linked.Token.ThrowIfCancellationRequested();
            lock (_pendingGate)
            {
                _pendingAuthorizationUri = authorizationUri;
                _pendingAuthorizationToken = linked.Token;
            }
            browserOpened?.Report(true);

            for (var attempt = 0; attempt < MaxCallbackAttempts; attempt++)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TimeoutException("The SSO authorization request timed out.");
                }

                using (client)
                {
                    var callback = await ReadCallbackAsync(
                        client,
                        state,
                        endpoints,
                        linked.Token).ConfigureAwait(false);
                    if (callback is null)
                    {
                        await WriteResponseAsync(client, HttpStatusCode.BadRequest, FailureHtml, linked.Token)
                            .ConfigureAwait(false);
                        continue;
                    }

                    await WriteResponseAsync(client, HttpStatusCode.OK, SuccessHtml, linked.Token)
                        .ConfigureAwait(false);
                    return new SsoAuthorizationCode(
                        callback.Value.Code,
                        verifier,
                        redirectUri,
                        callback.Value.State,
                        callback.Value.OrganizationIdentifier);
                }
            }

            throw new SsoAuthorizationException("Too many invalid SSO callback requests were received.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("The SSO authorization request timed out.");
        }
        finally
        {
            lock (_pendingGate)
            {
                _pendingAuthorizationUri = null;
                _pendingAuthorizationToken = default;
            }
            _gate.Release();
        }
    }

    public async Task<bool> ReopenPendingBrowserAsync(CancellationToken ct = default)
    {
        Task<bool>? launch = null;
        CancellationTokenSource? cancellation = null;
        try
        {
            lock (_pendingGate)
            {
                if (_pendingAuthorizationUri is null || _pendingAuthorizationToken.IsCancellationRequested)
                    return false;
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _pendingAuthorizationToken);
                launch = _launcher.LaunchAsync(_pendingAuthorizationUri, cancellation.Token);
            }
            return await launch.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SsoAuthorizationException("The system browser could not be reopened.", ex);
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    public static Uri BuildAuthorizationUri(
        ServerEndpoint webVault,
        string redirectUri,
        string state,
        string codeChallenge,
        string email,
        string? organizationIdentifier = null)
    {
        if (!webVault.IsInitialized)
            throw new ArgumentException("The web vault endpoint is not initialized.", nameof(webVault));
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeChallenge);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var query = string.Join('&',
            "clientId=desktop",
            $"redirectUri={Uri.EscapeDataString(redirectUri)}",
            $"state={Uri.EscapeDataString(state)}",
            $"codeChallenge={Uri.EscapeDataString(codeChallenge)}",
            $"email={Uri.EscapeDataString(email)}");
        // The web vault's existing SSO route accepts identifier to prefill and submit the organization.
        if (!string.IsNullOrWhiteSpace(organizationIdentifier))
            query += $"&identifier={Uri.EscapeDataString(organizationIdentifier.Trim())}";
        return new Uri($"{webVault.Value.TrimEnd('/')}/#/sso?{query}", UriKind.Absolute);
    }

    private static TcpListener BindListener()
    {
        var address = ResolveLocalhostAddress();
        SocketException? lastError = null;
        for (var port = FirstCallbackPort; port <= LastCallbackPort; port++)
        {
            var listener = new TcpListener(address, port);
            try
            {
                listener.Start(backlog: 4);
                return listener;
            }
            catch (SocketException ex)
            {
                lastError = ex;
                listener.Stop();
            }
        }

        throw new SsoAuthorizationException(
            $"No SSO callback port is available in {FirstCallbackPort}-{LastCallbackPort}.",
            lastError ?? new SocketException((int)SocketError.AddressAlreadyInUse));
    }

    private static IPAddress ResolveLocalhostAddress()
    {
        try
        {
            return Dns.GetHostAddresses("localhost").FirstOrDefault(IPAddress.IsLoopback)
                ?? IPAddress.Loopback;
        }
        catch (SocketException)
        {
            return IPAddress.Loopback;
        }
    }

    private static async Task<CallbackData?> ReadCallbackAsync(
        TcpClient client,
        string expectedState,
        ServerEndpoints endpoints,
        CancellationToken ct)
    {
        var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
            return null;

        var stream = client.GetStream();
        var header = await ReadHeaderAsync(stream, ct).ConfigureAwait(false);
        if (header is null)
            return null;

        var firstLineEnd = header.IndexOf("\r\n", StringComparison.Ordinal);
        var requestLine = firstLineEnd >= 0 ? header[..firstLineEnd] : header;
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !string.Equals(parts[0], "GET", StringComparison.Ordinal)
            || !parts[2].StartsWith("HTTP/", StringComparison.Ordinal)
            || !parts[1].StartsWith("/", StringComparison.Ordinal)
            || parts[1].StartsWith("//", StringComparison.Ordinal))
            return null;

        if (!Uri.TryCreate("http://localhost" + parts[1], UriKind.Absolute, out var requestUri)
            || requestUri.AbsolutePath != "/"
            || !TryParseUniqueQuery(requestUri.Query, out var query)
            || !query.TryGetValue("code", out var code)
            || !query.TryGetValue("state", out var receivedState)
            || string.IsNullOrWhiteSpace(code)
            || code.Length > 4096
            || (query.TryGetValue("iss", out var issuer) && !IsExpectedIssuer(issuer, endpoints))
            || !TryValidateState(expectedState, receivedState, out var identifier))
            return null;

        return new CallbackData(code, receivedState, identifier);
    }

    private static bool IsExpectedIssuer(string issuer, ServerEndpoints endpoints)
    {
        if (!ServerEndpoint.TryParse(issuer, out var parsed))
            return false;

        return parsed == endpoints.Identity
            || parsed == endpoints.WebVault
            || parsed.Value == endpoints.WebVault.Origin.Value;
    }

    private static async Task<string?> ReadHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1024];
        using var content = new MemoryStream();
        while (content.Length < MaxRequestHeaderBytes)
        {
            var remaining = (int)Math.Min(buffer.Length, MaxRequestHeaderBytes - content.Length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, remaining), ct).ConfigureAwait(false);
            if (read == 0)
                return null;
            content.Write(buffer, 0, read);

            var bytes = content.GetBuffer().AsSpan(0, (int)content.Length);
            if (bytes.IndexOf("\r\n\r\n"u8) >= 0)
                return Encoding.ASCII.GetString(bytes);
        }

        return null;
    }

    private static bool TryParseUniqueQuery(string rawQuery, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = WebUtility.UrlDecode(parts[0]);
            var value = WebUtility.UrlDecode(parts.Length == 2 ? parts[1] : string.Empty);
            if (string.IsNullOrEmpty(key) || !values.TryAdd(key, value))
                return false;
        }
        return true;
    }

    private static bool TryValidateState(
        string expectedState,
        string receivedState,
        out string? organizationIdentifier)
    {
        organizationIdentifier = null;
        if (string.Equals(receivedState, expectedState, StringComparison.Ordinal))
            return true;

        var prefix = expectedState + IdentifierMarker;
        if (!receivedState.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        organizationIdentifier = receivedState[prefix.Length..];
        return organizationIdentifier.Length <= 256;
    }

    private static async Task WriteResponseAsync(
        TcpClient client,
        HttpStatusCode status,
        string html,
        CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status == HttpStatusCode.OK ? "OK" : "Bad Request";
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)status} {reason}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; base-uri 'none'\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Connection: close\r\n\r\n");
        var stream = client.GetStream();
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string RandomBase64Url() => Base64Url(RandomNumberGenerator.GetBytes(RandomValueBytes));

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private const string SuccessHtml = "<!doctype html><html><head><meta charset=\"utf-8\"><title>SSO complete</title></head><body><h1>Authentication complete</h1><p>You can close this tab and return to WinVaultWarden.</p></body></html>";
    private const string FailureHtml = "<!doctype html><html><head><meta charset=\"utf-8\"><title>SSO callback rejected</title></head><body><h1>Authentication could not be completed</h1><p>Return to WinVaultWarden and try again.</p></body></html>";

    private readonly record struct CallbackData(
        string Code,
        string State,
        string? OrganizationIdentifier);
}
