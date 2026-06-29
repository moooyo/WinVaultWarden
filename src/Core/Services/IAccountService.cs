namespace Core.Services;

// 账户管理编排;改密/改 KDF 成功后强制登出(会话清空,需用新凭据重进)。要求 vault 解锁。
public interface IAccountService
{
    Task UpdateNameAsync(string name, CancellationToken ct = default);
    Task ChangePasswordAsync(string currentPassword, string newPassword, string? hint, CancellationToken ct = default);
    Task ChangeKdfAsync(string currentPassword, int newIterations, CancellationToken ct = default);
}
