namespace Core.Services;

/// <summary>
/// Raised before dispatch when another create for the same account and server is still in flight
/// or awaiting reconciliation.
/// </summary>
public sealed class CipherCreatePendingOperationException : InvalidOperationException
{
    public CipherCreatePendingOperationException()
        : base("Another cipher create is still in flight or awaiting reconciliation.")
    {
    }
}
