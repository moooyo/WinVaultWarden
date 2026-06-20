namespace Core.Services;

public interface IAuthService
{
    // 登录主线占位:prelogin → 派生 → connect/token。
    Task LoginAsync(string serverUrl, string email, string masterPassword, CancellationToken ct = default);
}
