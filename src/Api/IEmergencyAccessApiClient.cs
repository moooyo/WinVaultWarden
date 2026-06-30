using Api.Dtos;

namespace Api;

public interface IEmergencyAccessApiClient
{
    Task<ListResponse<EmergencyAccessGranteeDetailsDto>> GetTrustedAsync(CancellationToken ct = default);
    Task<ListResponse<EmergencyAccessGrantorDetailsDto>> GetGrantedAsync(CancellationToken ct = default);
    Task<PublicKeyResponse> GetPublicKeyAsync(string userId, CancellationToken ct = default);
    Task InviteAsync(EmergencyAccessInviteRequest request, CancellationToken ct = default);
    Task ReinviteAsync(string id, CancellationToken ct = default);
    Task AcceptAsync(string id, EmergencyAccessAcceptRequest request, CancellationToken ct = default);
    Task<EmergencyAccessDto> ConfirmAsync(string id, EmergencyAccessConfirmRequest request, CancellationToken ct = default);
    Task<EmergencyAccessDto> UpdateAsync(string id, EmergencyAccessUpdateRequest request, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<EmergencyAccessDto> InitiateAsync(string id, CancellationToken ct = default);
    Task<EmergencyAccessDto> ApproveAsync(string id, CancellationToken ct = default);
    Task<EmergencyAccessDto> RejectAsync(string id, CancellationToken ct = default);
    Task<EmergencyAccessViewResponse> ViewAsync(string id, CancellationToken ct = default);
    Task<EmergencyAccessTakeoverResponse> TakeoverAsync(string id, CancellationToken ct = default);
    Task PasswordAsync(string id, EmergencyAccessPasswordRequest request, CancellationToken ct = default);
    Task DeleteAccountAsync(DeleteAccountRequest request, CancellationToken ct = default);
}
