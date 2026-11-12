using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace App.Services;

public enum ReleaseUpdateStatus
{
    UpToDate,
    UpdateAvailable,
    Unavailable,
    Failed,
}

public sealed record ReleaseUpdateAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);

public sealed record ReleaseUpdateResult(
    ReleaseUpdateStatus Status,
    string CurrentVersion,
    string? LatestVersion,
    Uri? ReleasePage,
    ReleaseUpdateAsset? Asset,
    string Message);

/// <summary>
/// Checks published stable releases without credentials, redirects, downloads or execution.
/// Release metadata and checksums are not a substitute for installer signature verification.
/// </summary>
public sealed class ReleaseUpdateService : IDisposable
{
    public static readonly Uri ReleasesPage = new("https://github.com/moooyo/WinVaultWarden/releases");
    private static readonly Uri LatestEndpoint = new("https://api.github.com/repos/moooyo/WinVaultWarden/releases/latest");
    private const int MaximumResponseBytes = 512 * 1024;
    private const long MaximumAssetBytes = 2L * 1024 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public ReleaseUpdateService()
        : this(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
    {
    }

    public ReleaseUpdateService(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Limits optional automatic checks to one attempt per day. A future timestamp
    /// is treated as invalid so a clock correction cannot disable checks forever.
    /// Callers record the attempt time even when the network is unavailable.
    /// </summary>
    public static bool ShouldAutomaticallyCheck(DateTimeOffset now, DateTimeOffset? lastAttempt) =>
        lastAttempt is null || lastAttempt > now || now - lastAttempt.Value >= TimeSpan.FromDays(1);

    public async Task<ReleaseUpdateResult> CheckAsync(
        string currentVersion,
        string architecture,
        bool isPackaged,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!ReleaseVersion.TryParse(currentVersion, allowPackageRevision: true, out var current))
            return Result(ReleaseUpdateStatus.Failed, "The installed application version could not be determined.");
        var targetArchitecture = architecture?.ToLowerInvariant();
        if (targetArchitecture is not ("x64" or "arm64"))
            return Result(ReleaseUpdateStatus.Unavailable, "No release package is supported for this architecture.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestEndpoint);
            request.Headers.UserAgent.ParseAdd("WinVaultWarden-UpdateCheck/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);

            // The production handler disables automatic redirects. This also fails closed
            // if a supplied test or hosting handler changes the final request location.
            if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != LatestEndpoint)
                return Result(ReleaseUpdateStatus.Failed, "The update metadata location was not trusted.");
            if ((int)response.StatusCode is >= 300 and < 400)
                return Result(ReleaseUpdateStatus.Failed, "The update server redirected the request; no result was trusted.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return Result(ReleaseUpdateStatus.Unavailable, "The update service is rate limited. Try again later.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Result(ReleaseUpdateStatus.Unavailable, "No published stable release is available.");
            if (!response.IsSuccessStatusCode)
                return Result(ReleaseUpdateStatus.Unavailable, "The update service is currently unavailable.");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && mediaType != "application/json" && !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                return Result(ReleaseUpdateStatus.Failed, "The update service returned an unexpected response type.");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                return Result(ReleaseUpdateStatus.Failed, "The update metadata exceeded the allowed size.");

            var bytes = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize(bytes, ReleaseUpdateJsonContext.Default.ReleaseMetadata);
            if (release is null || release.Draft || release.Prerelease
                || !ReleaseVersion.TryParse(release.TagName, allowPackageRevision: false, out var latest)
                || latest.IsPrerelease || !TryReleasePage(release.HtmlUrl, release.TagName!, out var page))
                return Result(ReleaseUpdateStatus.Failed, "The published release metadata was invalid.");

            if (latest.CompareTo(current) <= 0)
                return new(ReleaseUpdateStatus.UpToDate, currentVersion, latest.Text, page, null,
                    "No newer stable release is available.");

            var expectedName = $"WinVaultWarden-{latest.Text}-{targetArchitecture}{(isPackaged ? ".msix" : "-portable.zip")}";
            var matches = release.Assets?.OfType<ReleaseAssetMetadata>().Where(asset =>
                string.Equals(asset.Name, expectedName, StringComparison.Ordinal)
                && asset.State == "uploaded"
                && asset.Size is > 0 and <= MaximumAssetBytes).ToArray() ?? [];
            if (matches.Length != 1 || !TryAssetUrl(matches[0].BrowserDownloadUrl, release.TagName!, expectedName, out var download))
                return new(ReleaseUpdateStatus.Unavailable, currentVersion, latest.Text, page, null,
                    "A newer release exists, but no compatible package is available. Open the official release page for details.");

            var asset = matches[0];
            var sha256 = TrySha256(asset.Digest);
            return new(ReleaseUpdateStatus.UpdateAvailable, currentVersion, latest.Text, page,
                new(expectedName, download!, asset.Size, sha256),
                "A newer stable release is available. Review its release notes and installation instructions before installing.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result(ReleaseUpdateStatus.Unavailable, "The update check timed out. Try again later.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Result(ReleaseUpdateStatus.Unavailable, "The update service could not be reached. Check the connection and try again.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return Result(ReleaseUpdateStatus.Failed, "The update metadata was invalid or exceeded the allowed size.");
        }

        ReleaseUpdateResult Result(ReleaseUpdateStatus status, string message) =>
            new(status, currentVersion ?? string.Empty, null, null, null, message);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var source = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("Release metadata is too large.");
            output.Write(buffer, 0, read);
        }
    }

    private static bool TryReleasePage(string? value, string tag, out Uri? uri) =>
        TryOfficialUri(value, $"/moooyo/WinVaultWarden/releases/tag/{Uri.EscapeDataString(tag)}", out uri);

    private static bool TryAssetUrl(string? value, string tag, string name, out Uri? uri) =>
        TryOfficialUri(value, $"/moooyo/WinVaultWarden/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}", out uri);

    private static bool TryOfficialUri(string? value, string path, out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri)
            && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
            && string.Equals(uri.IdnHost, "github.com", StringComparison.OrdinalIgnoreCase)
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && string.Equals(uri.AbsolutePath, path, StringComparison.Ordinal))
            return true;
        uri = null;
        return false;
    }

    private static string? TrySha256(string? digest)
    {
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
            return null;
        var hex = digest[7..];
        return hex.All(char.IsAsciiHexDigit) ? hex.ToLowerInvariant() : null;
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class ReleaseMetadata
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public ReleaseAssetMetadata[]? Assets { get; set; }
}

internal sealed class ReleaseAssetMetadata
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

[JsonSourceGenerationOptions(MaxDepth = 16)]
[JsonSerializable(typeof(ReleaseMetadata))]
internal partial class ReleaseUpdateJsonContext : JsonSerializerContext;

internal sealed record ReleaseVersion(int Major, int Minor, int Patch, string? Prerelease, string Text) : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => Prerelease is not null;

    public static bool TryParse(string? value, bool allowPackageRevision, out ReleaseVersion version)
    {
        version = null!;
        if (string.IsNullOrEmpty(value) || value.Length > 128)
            return false;
        var text = value.StartsWith('v') ? value[1..] : value;
        var metadataParts = text.Split('+');
        if (metadataParts.Length > 2 || (metadataParts.Length == 2 && !ValidIdentifiers(metadataParts[1], numericLeadingZerosAllowed: true)))
            return false;
        var releaseParts = metadataParts[0].Split('-', 2);
        var prerelease = releaseParts.Length == 2 ? releaseParts[1] : null;
        if (prerelease is not null && !ValidIdentifiers(prerelease, numericLeadingZerosAllowed: false))
            return false;
        var core = releaseParts[0].Split('.');
        if (allowPackageRevision && core.Length == 4 && core[3] == "0" && prerelease is null && metadataParts.Length == 1)
            core = core[..3];
        if (core.Length != 3 || !TryComponent(core[0], out var major)
            || !TryComponent(core[1], out var minor) || !TryComponent(core[2], out var patch))
            return false;
        var normalized = $"{major}.{minor}.{patch}" + (prerelease is null ? "" : $"-{prerelease}")
            + (metadataParts.Length == 2 ? $"+{metadataParts[1]}" : "");
        version = new(major, minor, patch, prerelease, normalized);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var difference = Major.CompareTo(other.Major);
        if (difference == 0) difference = Minor.CompareTo(other.Minor);
        if (difference == 0) difference = Patch.CompareTo(other.Patch);
        if (difference != 0) return difference;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var leftNumeric = left[index].All(char.IsAsciiDigit);
            var rightNumeric = right[index].All(char.IsAsciiDigit);
            difference = leftNumeric && rightNumeric
                ? (left[index].Length != right[index].Length
                    ? left[index].Length.CompareTo(right[index].Length)
                    : string.CompareOrdinal(left[index], right[index]))
                : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : string.CompareOrdinal(left[index], right[index]);
            if (difference != 0) return difference;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static bool TryComponent(string text, out int component)
    {
        component = 0;
        return text.Length is > 0 and <= 5 && (text.Length == 1 || text[0] != '0')
            && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out component)
            && component <= ushort.MaxValue;
    }

    private static bool ValidIdentifiers(string text, bool numericLeadingZerosAllowed)
    {
        foreach (var identifier in text.Split('.'))
        {
            if (identifier.Length == 0 || !identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
                return false;
            if (!numericLeadingZerosAllowed && identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsAsciiDigit))
                return false;
        }
        return true;
    }
}
