namespace Api;

/// <summary>
/// Identifies the request phase that stopped making progress before its timeout expired.
/// </summary>
public enum RequestTimeoutPhase
{
    ControlPlane,
    DownloadResponseHeaders,
    DownloadResponseBody,
    DownloadErrorResponseBody,
    UploadRequestBody,
    UploadResponseHeaders,
    UploadErrorResponseBody,
}

/// <summary>
/// Carries a stable, inspectable timeout reason while the outer exception can retain the standard
/// <see cref="TaskCanceledException"/> shape produced by <see cref="HttpClient"/>.
/// </summary>
public sealed class RequestTimeoutException : TimeoutException
{
    public RequestTimeoutException(
        RequestTimeoutPhase phase,
        TimeSpan timeout,
        Exception? innerException = null)
        : base($"The request made no progress during {phase} for {timeout}.", innerException)
    {
        Phase = phase;
        Timeout = timeout;
    }

    public RequestTimeoutPhase Phase { get; }

    public TimeSpan Timeout { get; }
}

/// <summary>
/// Distinguishes timeout-shaped cancellation from cancellation requested by the operation caller.
/// </summary>
public static class RequestTimeoutClassifier
{
    public static bool IsTimeout(
        Exception exception,
        CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (callerToken.IsCancellationRequested)
            return false;

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
                return true;
        }

        return false;
    }

    public static RequestTimeoutException? Find(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is RequestTimeoutException timeout)
                return timeout;
        }

        return null;
    }
}
