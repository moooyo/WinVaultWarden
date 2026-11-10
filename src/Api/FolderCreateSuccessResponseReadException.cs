using System.Net;

namespace Api;

/// <summary>
/// Raised after the server accepted a folder create request but its response did not
/// provide a usable folder ID. Synchronize before retrying to avoid duplicate folders.
/// </summary>
public sealed class FolderCreateSuccessResponseReadException : HttpRequestException
{
    private const string ErrorMessage =
        "The server accepted the folder create request, but its response could not be read.";

    public FolderCreateSuccessResponseReadException(HttpStatusCode successStatusCode, Exception innerException)
        : base(ErrorMessage, innerException ?? throw new ArgumentNullException(nameof(innerException)),
            ValidateSuccessStatus(successStatusCode))
    {
    }

    public FolderCreateSuccessResponseReadException(Exception innerException)
        : base(ErrorMessage, innerException ?? throw new ArgumentNullException(nameof(innerException)))
    {
    }

    private static HttpStatusCode ValidateSuccessStatus(HttpStatusCode statusCode)
    {
        if ((int)statusCode is < 200 or > 299)
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        return statusCode;
    }
}
