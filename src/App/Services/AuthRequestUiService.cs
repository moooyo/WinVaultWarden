using App.ViewModels.Models;
using Core.Services;

namespace App.Services;

/// <summary>
/// App 层授权请求 UI 服务接口。
/// 负责将 Core 的 IAuthRequestService（返回 Core.Models.PendingAuthRequest）
/// 映射为 UI 友好的 AuthRequestItem，UI 仅依赖本接口。
/// </summary>
public interface IAuthRequestUiService
{
    /// <summary>列出当前账户所有待审批的设备登录请求。</summary>
    Task<IReadOnlyList<AuthRequestItem>> ListPendingAsync(CancellationToken ct = default);

    /// <summary>批准指定登录请求。</summary>
    /// <param name="id">auth-request UUID。</param>
    /// <param name="publicKey">发起方公钥（Base64），用于构造加密响应体。</param>
    Task ApproveAsync(string id, string publicKey, CancellationToken ct = default);

    /// <summary>拒绝指定登录请求。</summary>
    /// <param name="id">auth-request UUID。</param>
    Task DenyAsync(string id, CancellationToken ct = default);
}

/// <summary>
/// IAuthRequestUiService 的真实实现，委托到 Core.Services.IAuthRequestService。
/// 若注入的 IAuthRequestService 为 null，抛出 AuthRequestOperationException
/// （DevicesViewModel 负责捕获并写 Error）。
/// </summary>
public sealed class AuthRequestUiService : IAuthRequestUiService
{
    private readonly IAuthRequestService? _service;

    public AuthRequestUiService(IAuthRequestService? service = null)
    {
        _service = service;
    }

    private IAuthRequestService Require() =>
        _service ?? throw new AuthRequestOperationException("设备登录授权服务不可用");

    public async Task<IReadOnlyList<AuthRequestItem>> ListPendingAsync(CancellationToken ct = default)
    {
        var requests = await Require().ListPendingAsync(ct);
        return requests.Select(Map).ToList();
    }

    public Task ApproveAsync(string id, string publicKey, CancellationToken ct = default) =>
        Require().ApproveAsync(id, publicKey, ct);

    public Task DenyAsync(string id, CancellationToken ct = default) =>
        Require().DenyAsync(id, ct);

    private static AuthRequestItem Map(Core.Models.PendingAuthRequest r) =>
        new(
            r.Id,
            r.DeviceTypeName,
            r.IpAddress,
            FormatCreatedLabel(r.CreationDate),
            r.PublicKey);

    private static string FormatCreatedLabel(string creationDate)
    {
        if (DateTimeOffset.TryParse(creationDate, out var dto))
            return dto.ToLocalTime().ToString("yyyy/M/d HH:mm");
        return creationDate;
    }
}
