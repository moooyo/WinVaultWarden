using Core.Models;

namespace Core.Services;

public interface IVaultHealthService
{
    HealthReport AnalyzeOffline();
    Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default);
}
