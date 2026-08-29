using System.Net;

namespace Api;

/// <summary>
/// The server returned a response proving that a replacement DELETE did not remove the Send.
/// It is safe for the caller to roll back the newly uploaded replacement.
/// </summary>
public sealed class SendDeleteDefinitelyNotAppliedException : VaultApiException
{
    public SendDeleteDefinitelyNotAppliedException(
        string sendId,
        HttpStatusCode statusCode,
        string? serverMessage)
        : base(
            statusCode,
            serverMessage,
            serverMessage ?? $"Send deletion was rejected ({(int)statusCode}).") =>
        SendId = sendId;

    public string SendId { get; }
}

/// <summary>
/// A replacement DELETE may have reached the server, but its final effect cannot be proven.
/// The newly uploaded replacement must be preserved.
/// </summary>
public sealed class SendDeleteOutcomeUncertainException : HttpRequestException
{
    public SendDeleteOutcomeUncertainException(
        string sendId,
        Exception innerException,
        HttpStatusCode? statusCode = null)
        : base(
            "The previous Send deletion may have completed, but no conclusive response was received.",
            innerException,
            statusCode) =>
        SendId = sendId;

    public string SendId { get; }
}
