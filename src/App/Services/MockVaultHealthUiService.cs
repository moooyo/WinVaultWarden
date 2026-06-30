using Core.Models;

namespace App.Services;

/// <summary>
/// IVaultHealthUiService 的内存替身，用于设计期和测试。不触网。
/// AnalyzeOffline 返回含 ≥1 ReusedGroup、≥1 WeakFinding、≥1 UnsecuredFinding 的 HealthReport；
/// CheckExposedAsync 返回 ≥1 ExposedFinding。
/// </summary>
public sealed class MockVaultHealthUiService : IVaultHealthUiService
{
    private static readonly HealthItemRef _ref1 = new("cipher-1", "GitHub", "user@example.com");
    private static readonly HealthItemRef _ref2 = new("cipher-2", "Twitter", "user@example.com");
    private static readonly HealthItemRef _ref3 = new("cipher-3", "OldBank", null);

    private static readonly HealthReport _report = new(
        Reused:
        [
            new ReusedGroup(Count: 2, Items: [_ref1, _ref2]),
        ],
        Weak:
        [
            new WeakFinding(Item: _ref3, Score: 12),
        ],
        Unsecured:
        [
            new UnsecuredFinding(Item: _ref3, Uri: "http://oldbank.example.com/login"),
        ]);

    private static readonly IReadOnlyList<ExposedFinding> _exposed =
    [
        new ExposedFinding(Item: _ref1, BreachCount: 5),
    ];

    public HealthReport AnalyzeOffline() => _report;

    public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default) =>
        Task.FromResult(_exposed);
}
