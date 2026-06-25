namespace Core.Services;

public sealed record PasskeyApprovalRequest(
    string RequestId,
    string Origin,
    string RpId,
    string CipherId,
    string CipherName,
    string? UserName,
    string? UserDisplayName);

public interface IPasskeyApprovalService
{
    Task<bool> ConfirmUseAsync(PasskeyApprovalRequest request, CancellationToken ct = default);
}
