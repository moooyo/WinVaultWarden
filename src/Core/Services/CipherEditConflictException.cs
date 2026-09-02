namespace Core.Services;

/// <summary>
/// Raised when an edit is based on a different cipher revision or ownership baseline.
/// </summary>
public sealed class CipherEditConflictException : InvalidOperationException
{
    public CipherEditConflictException(string message)
        : base(message)
    {
    }
}
