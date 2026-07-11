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
/// 全局未处理 UI 异常的"可继续"白名单策略(纯逻辑、可单测)。
/// 仅当异常是显式的取消(<see cref="OperationCanceledException"/>,含 <c>TaskCanceledException</c>)
/// 或显式可恢复(<see cref="RecoverableUiException"/>)时才允许继续;其余(含把未知异常包进
/// <see cref="AggregateException"/> 的情形)一律不可继续,交由调用方 fail-closed 后终止。
/// 不对 <see cref="AggregateException"/> 解包——包装未知异常不能"洗白"为可恢复。
/// </summary>
public static class UiExceptionPolicy
{
    public static bool CanContinue(Exception exception) =>
        exception is OperationCanceledException
            or RecoverableUiException;
}
