using System.Net;

namespace Api;

/// <summary>
/// The account deletion endpoint returned a successful HTTP status outside its documented
/// 200/204 contract, so the server-side outcome cannot be treated as confirmed.
/// </summary>
public sealed class AccountDeletionOutcomeUncertainException : HttpRequestException
{
    public AccountDeletionOutcomeUncertainException(HttpStatusCode statusCode)
        : base(
            $"Account deletion returned the unexpected success status {(int)statusCode}.",
            null,
            Validate(statusCode))
    {
    }

    private static HttpStatusCode Validate(HttpStatusCode statusCode)
    {
        if ((int)statusCode is < 200 or > 299
            || statusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }
        return statusCode;
    }
}
