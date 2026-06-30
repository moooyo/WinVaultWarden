using Core.Models;

namespace Core.Services;

public interface IEmergencyAccessService
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
