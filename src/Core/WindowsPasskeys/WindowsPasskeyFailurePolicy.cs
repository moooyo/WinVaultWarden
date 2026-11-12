namespace Core.WindowsPasskeys;

/// <summary>Maps protocol failures without relabelling a network timeout as user cancellation.</summary>
public static class WindowsPasskeyFailurePolicy
{
    public const int RequestTimeout = unchecked((int)0x800705B4);
    public const int UnexpectedFailure = unchecked((int)0x80004005);

    public static int ToHResult(Exception exception, CancellationToken requestToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is WindowsPasskeyException passkey)
            return passkey.ErrorCode;
        if (exception is OperationCanceledException && requestToken.IsCancellationRequested)
            return WindowsPasskeyException.UserCancelled;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
                return RequestTimeout;
        }
        // A cancelled token owned by another operation does not prove that this request
        // was cancelled. In particular, HttpClient also uses TaskCanceledException.
        return UnexpectedFailure;
    }
}
