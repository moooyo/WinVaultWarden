namespace Core.Services;

/// <summary>
/// Raised when account-bound persistence is denied because account-deletion coordination or
/// durable deletion state cannot safely permit the write.
/// </summary>
public sealed class AccountDeletionWriteBlockedException : IOException
{
    private const string DefaultMessage =
        "Account-bound persistence is blocked by account-deletion recovery state.";

    public AccountDeletionWriteBlockedException()
        : base(DefaultMessage)
    {
    }

    public AccountDeletionWriteBlockedException(Exception innerException)
        : base(DefaultMessage, innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
    }
}
