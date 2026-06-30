using Core.Models;
using Core.Services;

namespace App.Services;

/// <summary>
/// App 层紧急访问 UI 服务接口。
/// 方法签名与 IEmergencyAccessService 相同：Core 模型
/// (EmergencyContact / GrantedAccess / RecoveredVault) 已适合直接展示，无需额外映射。
/// </summary>
public interface IEmergencyAccessUiService
{
    // 授予方
    Task<IReadOnlyList<EmergencyContact>> GetTrustedAsync(CancellationToken ct = default);
    Task InviteAsync(string email, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default);
    Task ReinviteAsync(string id, CancellationToken ct = default);
    Task ConfirmAsync(string id, string granteeId, CancellationToken ct = default);
    Task UpdateAsync(string id, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default);
    Task RemoveAsync(string id, CancellationToken ct = default);
    Task ApproveAsync(string id, CancellationToken ct = default);
    Task RejectAsync(string id, CancellationToken ct = default);
    // 受托方
    Task<IReadOnlyList<GrantedAccess>> GetGrantedAsync(CancellationToken ct = default);
    Task AcceptAsync(string id, string token, CancellationToken ct = default);
    Task InitiateAsync(string id, CancellationToken ct = default);
    Task<RecoveredVault> ViewAsync(string id, string grantorEmail, CancellationToken ct = default);
    Task TakeoverAndResetPasswordAsync(string id, string grantorEmail, string newPassword, CancellationToken ct = default);
}

/// <summary>
/// IEmergencyAccessUiService 的真实实现：1:1 转发给 Core.IEmergencyAccessService。
/// Core 模型已适合展示，此处不做任何映射。
/// </summary>
public sealed class EmergencyAccessUiService : IEmergencyAccessUiService
{
    private readonly IEmergencyAccessService _service;

    public EmergencyAccessUiService(IEmergencyAccessService service)
    {
        _service = service;
    }

    public Task<IReadOnlyList<EmergencyContact>> GetTrustedAsync(CancellationToken ct = default) =>
        _service.GetTrustedAsync(ct);

    public Task InviteAsync(string email, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default) =>
        _service.InviteAsync(email, type, waitTimeDays, ct);

    public Task ReinviteAsync(string id, CancellationToken ct = default) =>
        _service.ReinviteAsync(id, ct);

    public Task ConfirmAsync(string id, string granteeId, CancellationToken ct = default) =>
        _service.ConfirmAsync(id, granteeId, ct);

    public Task UpdateAsync(string id, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default) =>
        _service.UpdateAsync(id, type, waitTimeDays, ct);

    public Task RemoveAsync(string id, CancellationToken ct = default) =>
        _service.RemoveAsync(id, ct);

    public Task ApproveAsync(string id, CancellationToken ct = default) =>
        _service.ApproveAsync(id, ct);

    public Task RejectAsync(string id, CancellationToken ct = default) =>
        _service.RejectAsync(id, ct);

    public Task<IReadOnlyList<GrantedAccess>> GetGrantedAsync(CancellationToken ct = default) =>
        _service.GetGrantedAsync(ct);

    public Task AcceptAsync(string id, string token, CancellationToken ct = default) =>
        _service.AcceptAsync(id, token, ct);

    public Task InitiateAsync(string id, CancellationToken ct = default) =>
        _service.InitiateAsync(id, ct);

    public Task<RecoveredVault> ViewAsync(string id, string grantorEmail, CancellationToken ct = default) =>
        _service.ViewAsync(id, grantorEmail, ct);

    public Task TakeoverAndResetPasswordAsync(string id, string grantorEmail, string newPassword, CancellationToken ct = default) =>
        _service.TakeoverAndResetPasswordAsync(id, grantorEmail, newPassword, ct);
}
