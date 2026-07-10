namespace Core.Models;

/// <summary>
/// 在线 HIBP 泄露检查的显式完成状态。绝不能把"未知"或"部分/全部失败"
/// 伪装成"已确认零泄露"——Succeeded 且 Findings 为空才代表真正的零泄露。
/// </summary>
public enum BreachCheckStatus
{
    /// <summary>尚未执行在线检查(UI/报告初态)。</summary>
    Unknown,

    /// <summary>所有唯一密码均已成功查询 HIBP。</summary>
    Succeeded,

    /// <summary>至少一次查询失败,但至少一次成功;已知 Findings 予以保留。</summary>
    PartialFailure,

    /// <summary>全部查询失败,没有任何可信结果。</summary>
    Failed,
}

/// <summary>
/// HIBP 泄露检查结果,显式携带成功/失败查询计数,
/// 使调用方能区分"确认未泄露"与"检查不完整"。
/// </summary>
public sealed record BreachCheckResult(
    BreachCheckStatus Status,
    IReadOnlyList<ExposedFinding> Findings,
    int SuccessfulQueries,
    int FailedQueries)
{
    public static BreachCheckResult Unknown { get; } =
        new(BreachCheckStatus.Unknown, [], 0, 0);
}
