namespace Core.Models;

public sealed record HealthItemRef(string CipherId, string Name, string? Username);
public sealed record ReusedGroup(int Count, IReadOnlyList<HealthItemRef> Items);
public sealed record WeakFinding(HealthItemRef Item, int Score);
public sealed record UnsecuredFinding(HealthItemRef Item, string Uri);
public sealed record ExposedFinding(HealthItemRef Item, int BreachCount);
public sealed record HealthReport(
    IReadOnlyList<ReusedGroup> Reused,
    IReadOnlyList<WeakFinding> Weak,
    IReadOnlyList<UnsecuredFinding> Unsecured)
{
    // 尚未执行在线 HIBP 检查时的初态；离线报告不得暗示"已确认零泄露"。
    public BreachCheckResult Exposure { get; init; } = BreachCheckResult.Unknown;
}
