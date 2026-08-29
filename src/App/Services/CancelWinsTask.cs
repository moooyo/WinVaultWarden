namespace App.Services;

/// <summary>
/// Adds cancel-wins ownership semantics to an operation that cannot itself be cancelled. When the
/// caller stops waiting, a successful late result is released and a late fault is observed.
/// </summary>
public static class CancelWinsTask
{
    public static async Task<T> AwaitAsync<T>(
        Task<T> operation,
        Func<T, ValueTask> releaseLateResult,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(releaseLateResult);
        ct.ThrowIfCancellationRequested();

        T result;
        try
        {
            result = await operation.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _ = ObserveAndReleaseAsync(operation, releaseLateResult);
            throw;
        }

        if (!ct.IsCancellationRequested)
            return result;

        await ReleaseSafelyAsync(result, releaseLateResult).ConfigureAwait(false);
        throw new OperationCanceledException(ct);
    }

    private static async Task ObserveAndReleaseAsync<T>(
        Task<T> operation,
        Func<T, ValueTask> releaseLateResult)
    {
        try
        {
            var result = await operation.ConfigureAwait(false);
            await ReleaseSafelyAsync(result, releaseLateResult).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The caller has already observed cancellation. This continuation exists to observe a
            // late fault or release a late successful result, so it must never become unobserved.
        }
    }

    private static async ValueTask ReleaseSafelyAsync<T>(
        T result,
        Func<T, ValueTask> releaseLateResult)
    {
        try
        {
            await releaseLateResult(result).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A cleanup failure cannot turn a completed cancellation into a late success.
        }
    }
}
