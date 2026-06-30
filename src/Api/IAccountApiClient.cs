using Api.Dtos;

namespace Api;

// 账户管理端点(/api 或 /identity 前缀;register 走 /identity),空 200 响应,失败抛 VaultWriteException。
public interface IAccountApiClient
{
    void SetBaseAddress(string baseUrl);
    Task UpdateProfileAsync(ProfileUpdateRequest request, CancellationToken ct = default);
    Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default);
    Task ChangeKdfAsync(ChangeKdfRequest request, CancellationToken ct = default);
    Task RegisterAsync(RegisterRequest request, CancellationToken ct = default);
}
