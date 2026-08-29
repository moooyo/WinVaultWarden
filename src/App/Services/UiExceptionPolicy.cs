namespace App.Services;

/// <summary>
/// 显式标注"可恢复"的 UI 异常:UI 层在明确知道某次操作失败可安全继续(不损坏会话/密钥状态)时抛出。
/// 只有这类异常(及取消)才允许全局兜底把 <c>Handled</c> 置真、让进程继续运行。
/// 未包装成本类型的任意异常一律视为未知,必须 fail-closed。
/// </summary>
public sealed class RecoverableUiException : Exception
{
    public RecoverableUiException(string message) : base(message) { }

    public RecoverableUiException(string message, Exception inner)
        : base(message, inner) { }
}

/// <summary>
/// Allows the global UI boundary to continue only for explicit recovery or genuine cancellation.
/// A timeout-shaped <see cref="TaskCanceledException"/> is a failure, not user cancellation, and
/// must be handled by the operation surface. Aggregate exceptions are never unwrapped here.
/// </summary>
public static class UiExceptionPolicy
{
    public const string RequestTimeoutMessage = "网络请求超时，请检查连接后重试。";

    public static bool IsGenuineCancellation(
        OperationCanceledException exception,
        CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (callerToken.IsCancellationRequested)
            return true;

        return !Api.RequestTimeoutClassifier.IsTimeout(exception)
            && exception.CancellationToken.IsCancellationRequested;
    }

    public static bool CanContinue(Exception exception) =>
        (exception is OperationCanceledException cancellation
            && IsGenuineCancellation(cancellation))
        || exception is RecoverableUiException;
}
