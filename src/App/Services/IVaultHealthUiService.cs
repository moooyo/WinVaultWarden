using Core.Models;
using Core.Services;

namespace App.Services;

/// <summary>离线安全分析进度快照,用于驱动 Loading 面板的 ProgressBar/文案。</summary>
public sealed record HealthAnalysisProgress(
    int Completed,
    int Total,
    string Message);

/// <summary>
/// 离线安全分析失败时抛出的包装异常。ViewModel 只捕获此类型来驱动 Error 态,
/// 避免把取消(OperationCanceledException)或其他基础设施异常误判为"分析失败"。
/// </summary>
public sealed class VaultHealthUiException(string message, Exception inner)
    : Exception(message, inner);

/// <summary>
/// App 层 Vault 健康报告 UI 服务接口。
/// AnalyzeOfflineAsync 在后台线程对不可变会话快照执行 CPU 密集分析,不阻塞 UI 线程;
/// 提交结果前会重新校验捕获的会话租约,Lock/切账户/登出发生的旧结果一律被丢弃。
/// </summary>
public interface IVaultHealthUiService
{
    Task<HealthReport> AnalyzeOfflineAsync(
        IProgress<HealthAnalysisProgress>? progress = null,
        CancellationToken ct = default);
    Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default);
}

/// <summary>
/// IVaultHealthUiService 的真实实现。捕获一次会话租约,把 CPU 密集的
/// <see cref="IVaultHealthService.AnalyzeOffline"/> 送入 <see cref="Task.Run(Action)"/>
/// 在线程池执行,提交结果前用同一租约校验会话仍然当前(未被 Lock/登出/切账户轮换)。
/// </summary>
public sealed class VaultHealthUiService(
    IVaultHealthService service,
    Vault.VaultSession session) : IVaultHealthUiService
{
    public async Task<HealthReport> AnalyzeOfflineAsync(
        IProgress<HealthAnalysisProgress>? progress = null,
        CancellationToken ct = default)
    {
        var lease = session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            lease.CancellationToken);
        progress?.Report(new HealthAnalysisProgress(0, 100, "正在分析密码库"));
        HealthReport report;
        try
        {
            report = await Task.Run(
                () =>
                {
                    linked.Token.ThrowIfCancellationRequested();
                    return service.AnalyzeOffline();
                },
                linked.Token);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException)
        {
            throw new VaultHealthUiException("本地安全报告分析失败", ex);
        }

        session.ThrowIfExpired(lease);
        progress?.Report(new HealthAnalysisProgress(100, 100, "分析完成"));
        return report;
    }

    public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default) =>
        service.CheckExposedAsync(ct);
}
