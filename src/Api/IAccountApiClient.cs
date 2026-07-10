using Api.Dtos;

namespace Api;

// 账户管理端点(/api 前缀),空 200 响应,失败抛 VaultWriteException。
public interface IAccountApiClient : IAuthenticatedApiRole
{
    Task UpdateProfileAsync(ProfileUpdateRequest request, CancellationToken ct = default);
    Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default);
    Task ChangeKdfAsync(ChangeKdfRequest request, CancellationToken ct = default);
}
