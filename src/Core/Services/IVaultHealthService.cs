using Core.Models;

namespace Core.Services;

public interface IVaultHealthService
{
    HealthReport AnalyzeOffline();

    /// <summary>
    /// 执行在线 HIBP 泄露检查,返回显式状态(Succeeded/PartialFailure/Failed)
    /// 与成功/失败查询计数,绝不把失败伪装成零泄露。
    /// </summary>
    Task<BreachCheckResult> CheckExposureAsync(CancellationToken ct = default);

    /// <summary>
    /// 兼容方法:只有 <see cref="BreachCheckStatus.Succeeded"/> 时返回 Findings;
    /// 其余状态抛出携带结果的 <see cref="BreachCheckIncompleteException"/>。
    /// </summary>
    Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default);
}
