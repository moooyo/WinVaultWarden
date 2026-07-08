namespace Core.Services;

public sealed record PasskeyLoginTarget(string CipherId, string Name);

public sealed record PasskeyApprovalRequest(
    string RequestId,
    string Origin,
    string RpId,
    string CipherId,
    string CipherName,
    string? UserName,
    string? UserDisplayName);

public sealed record PasskeyCreateApprovalRequest(
    string RequestId,
    string Origin,
    string RpId,
    string? RpName,
    string? UserName,
    string? UserDisplayName,
    IReadOnlyList<PasskeyLoginTarget> ExistingLogins);

public sealed record PasskeyCreateApprovalResult(bool Approved, bool CreateNew, string? CipherId);

public interface IPasskeyApprovalService
{
    Task<bool> ConfirmUseAsync(PasskeyApprovalRequest request, CancellationToken ct = default);

    /// <summary>
    /// 确认是否创建新的 passkey，或将其附加到现有登录条目。
    /// </summary>
    Task<PasskeyCreateApprovalResult> ConfirmCreateAsync(PasskeyCreateApprovalRequest request, CancellationToken ct = default);
}

