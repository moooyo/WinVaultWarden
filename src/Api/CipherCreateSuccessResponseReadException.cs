using System.Net;

namespace Api;

/// <summary>
/// Raised when a cipher create request received a successful HTTP status but its response body
/// could not be read or parsed. The server-side commit must be treated as confirmed until an
/// authoritative reconciliation proves otherwise.
/// </summary>
public sealed class CipherCreateSuccessResponseReadException : HttpRequestException
{
    public CipherCreateSuccessResponseReadException(
        HttpStatusCode successStatusCode,
        Exception innerException)
        : base(
            "The server accepted the cipher create request, but its response could not be read.",
            innerException ?? throw new ArgumentNullException(nameof(innerException)),
            ValidateSuccessStatus(successStatusCode))
    {
        SuccessStatusCode = successStatusCode;
    }

    public HttpStatusCode SuccessStatusCode { get; }

    private static HttpStatusCode ValidateSuccessStatus(HttpStatusCode statusCode)
    {
        if ((int)statusCode is < 200 or > 299)
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        return statusCode;
    }
}
