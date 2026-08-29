using Core.Session;

namespace Core.Services;

public interface IAuthService
{
    AuthenticationAttemptLease BeginAuthenticationAttempt();
    void AbandonAuthenticationAttempt(AuthenticationAttemptLease attempt);
    Task<AuthResult> LoginAsync(AuthenticationAttemptLease attempt, string serverUrl, string email, string masterPassword, CancellationToken ct = default);
    Task<AuthResult> SubmitTwoFactorAsync(AuthenticationAttemptLease attempt, string code, int provider, CancellationToken ct = default, bool rememberDevice = true);
    Task<bool> ResendTwoFactorEmailAsync(AuthenticationAttemptLease attempt, CancellationToken ct = default);
    Task<AuthResult> UnlockAsync(AuthenticationAttemptLease attempt, string masterPassword, CancellationToken ct = default);
    Task<AuthResult> UnlockWithPinAsync(AuthenticationAttemptLease attempt, string pin, CancellationToken ct = default);
    Task<AuthResult> UnlockWithHelloAsync(AuthenticationAttemptLease attempt, byte[] signature, CancellationToken ct = default);
    Task LockAsync(CancellationToken ct = default);
    Task LockAsync(VaultSessionLease expectedSession, CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
    Task<bool> LogoutAsync(VaultSessionLease expectedSession, CancellationToken ct = default);
}

public enum AuthFailureKind
{
    Unspecified,
    InvalidCredentials,
    Network,
    Timeout,
    InvalidResponse,
    SessionExpired,
    NoSavedSession,
    SafeMessage,
}

public abstract record AuthResult
{
    public sealed record Success(VaultSessionLease Lease) : AuthResult;
    public sealed record TwoFactorRequired(IReadOnlyList<int> Providers) : AuthResult;
    public sealed record Failure(
        string Message,
        AuthFailureKind Kind = AuthFailureKind.Unspecified) : AuthResult;
    public sealed record PinCleared(string Message) : AuthResult;
}
