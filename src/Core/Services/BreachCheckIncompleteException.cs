using Core.Models;

namespace Core.Services;

/// <summary>
/// 在线 HIBP 泄露检查未能完整完成时抛出(Unknown/PartialFailure/Failed)。
/// 兼容方法 <see cref="IVaultHealthService.CheckExposedAsync"/> 只在
/// <see cref="BreachCheckStatus.Succeeded"/> 时返回结果,其余状态一律抛出本异常,
/// 绝不能把不完整的检查解释为"零泄露"。
/// </summary>
public sealed class BreachCheckIncompleteException : Exception
{
    public BreachCheckIncompleteException(BreachCheckResult result)
        : base("在线泄露检查未完整完成；结果不能解释为零泄露。") =>
        Result = result;

    public BreachCheckResult Result { get; }
}
