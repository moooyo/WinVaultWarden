namespace Core.Services;

public interface IAuthService
{
    Task<AuthResult> LoginAsync(string serverUrl, string email, string masterPassword, CancellationToken ct = default);
    Task<AuthResult> SubmitTwoFactorAsync(string code, CancellationToken ct = default, bool rememberDevice = true);
    Task<AuthResult> UnlockAsync(string masterPassword, CancellationToken ct = default);
    Task<AuthResult> UnlockWithPinAsync(string pin, CancellationToken ct = default);
    Task LockAsync(CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
}

public abstract record AuthResult
{
    public sealed record Success : AuthResult;
    public sealed record TwoFactorRequired(IReadOnlyList<int> Providers) : AuthResult;
    public sealed record Failure(string Message) : AuthResult;
    public sealed record PinCleared(string Message) : AuthResult;
}
