namespace Core.Services;

/// <summary>
/// Supplies the inactivity deadline used by streaming file reads and writes. The deadline restarts
/// for every asynchronous I/O operation, so a progressing transfer has no fixed total duration.
/// </summary>
public interface ITransferIdleTimeoutPolicy
{
    TimeSpan IdleTimeout { get; }
}

public sealed class TransferIdleTimeoutPolicy : ITransferIdleTimeoutPolicy
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(30);

    public TransferIdleTimeoutPolicy(TimeSpan idleTimeout)
    {
        if (idleTimeout != Timeout.InfiniteTimeSpan && idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));

        IdleTimeout = idleTimeout;
    }

    public TimeSpan IdleTimeout { get; }
}
