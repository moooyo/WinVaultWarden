using Core.Models;
using Core.Services;

namespace App.Services;

/// <summary>
/// App 层 Vault 健康报告 UI 服务接口。
/// 方法签名与 IVaultHealthService 相同：Core 模型
/// (HealthReport / ExposedFinding) 已适合直接展示，无需额外映射。
/// </summary>
public interface IVaultHealthUiService
{
    HealthReport AnalyzeOffline();
    Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default);
}

/// <summary>
/// IVaultHealthUiService 的真实实现：1:1 转发给 Core.IVaultHealthService。
/// Core 模型已适合展示，此处不做任何映射。
/// </summary>
public sealed class VaultHealthUiService : IVaultHealthUiService
{
    private readonly IVaultHealthService _service;

    public VaultHealthUiService(IVaultHealthService service)
    {
        _service = service;
    }

    public HealthReport AnalyzeOffline() =>
        _service.AnalyzeOffline();

    public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default) =>
        _service.CheckExposedAsync(ct);
}
