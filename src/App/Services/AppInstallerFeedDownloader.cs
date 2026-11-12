using System.Net;

namespace App.Services;

/// <summary>Downloads bounded public feed metadata without credentials or implicit redirects.</summary>
public sealed class AppInstallerFeedDownloader : IDisposable
{
    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;

    public AppInstallerFeedDownloader()
        : this(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
    {
    }

    public AppInstallerFeedDownloader(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<byte[]> DownloadAsync(string architecture, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AppInstallerFeedPolicy.TryGetFeedUri(architecture, out var source))
            throw new ArgumentException("No official update feed exists for this architecture.", nameof(architecture));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            var current = source!;
            for (var redirects = 0; redirects <= 4; redirects++)
            {
                if (!AppInstallerFeedPolicy.IsAllowedDownloadLocation(current, architecture))
                    throw new InvalidDataException("The update feed redirected to an untrusted location.");
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.ParseAdd("WinVaultWarden-AppInstaller/1.0");
                using var response = await _client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is { } final && final != current)
                    throw new InvalidDataException("The update transport followed an unexpected redirect.");
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    if (redirects == 4 || response.Headers.Location is not { } location)
                        throw new InvalidDataException("The update feed exceeded its redirect limit.");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > AppInstallerFeedPolicy.MaximumFeedBytes)
                    throw new InvalidDataException("The update feed was too large.");
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var result = new MemoryStream();
                var buffer = new byte[4096];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                    if (read == 0)
                        return result.ToArray();
                    if (result.Length + read > AppInstallerFeedPolicy.MaximumFeedBytes)
                        throw new InvalidDataException("The update feed was too large.");
                    result.Write(buffer, 0, read);
                }
            }
            throw new InvalidDataException("The update feed exceeded its redirect limit.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The App Installer feed request timed out.", exception);
        }
    }

    public void Dispose() => _client.Dispose();
}
