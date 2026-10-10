using Core.Session;

namespace Core.Services;

public interface IAuthService
{
    AuthenticationAttemptLease BeginAuthenticationAttempt();
    void AbandonAuthenticationAttempt(AuthenticationAttemptLease attempt);
    Task RequestPasswordHintAsync(
        string serverUrl,
        string email,
        CancellationToken ct = default) =>
        Task.FromException(
            new NotSupportedException("Password hints are unavailable."));
    Task<bool> IsLoginWithDeviceAvailableAsync(
        string serverUrl,
        string email,
        CancellationToken ct = default) => Task.FromResult(false);
    Task<LoginWithDeviceQuarantineInfo> GetLoginWithDeviceQuarantineInfoAsync(
        string serverUrl,
        string email,
        CancellationToken ct = default) =>
        Task.FromResult<LoginWithDeviceQuarantineInfo>(
            new LoginWithDeviceQuarantineInfo.Unavailable());
    Task<LoginWithDeviceQuarantineClearResult> ClearLoginWithDeviceQuarantineAsync(
        string serverUrl,
        string email,
        LoginWithDeviceQuarantineInfo.Found expected,
        CancellationToken ct = default) =>
        Task.FromResult(new LoginWithDeviceQuarantineClearResult(
            LoginWithDeviceQuarantineClearStatus.Unavailable));
    Task<AuthResult> BeginLoginWithDeviceAsync(
        AuthenticationAttemptLease attempt,
        string serverUrl,
        string email,
        CancellationToken ct = default) =>
        Task.FromResult<AuthResult>(new AuthResult.Failure(
            "Login with device is not available.",
            AuthFailureKind.SafeMessage));
    Task<AuthResult> PollLoginWithDeviceAsync(
        AuthenticationAttemptLease attempt,
        CancellationToken ct = default) =>
        Task.FromResult<AuthResult>(new AuthResult.Failure(
            "No login-with-device request is pending.",
            AuthFailureKind.SessionExpired));
    LoginWithDeviceCancellationResult CancelLoginWithDevice(
        AuthenticationAttemptLease attempt)
    {
        AbandonAuthenticationAttempt(attempt);
        return new LoginWithDeviceCancellationResult(
            LoginWithDeviceCancellationStatus.Cancelled);
    }
    Task<AuthResult> LoginAsync(AuthenticationAttemptLease attempt, string serverUrl, string email, string masterPassword, CancellationToken ct = default);
    Task<AuthResult> LoginWithSsoAsync(AuthenticationAttemptLease attempt, string serverUrl, string email, SsoAuthorizationCode authorization, CancellationToken ct = default) =>
        Task.FromResult<AuthResult>(new AuthResult.Failure(
            "SSO authentication is not available.",
            AuthFailureKind.UnsupportedSsoDecryption));
    Task<AuthResult> SubmitTwoFactorAsync(AuthenticationAttemptLease attempt, string code, int provider, CancellationToken ct = default, bool rememberDevice = true);
    Task<bool> ResendTwoFactorEmailAsync(AuthenticationAttemptLease attempt, CancellationToken ct = default);
    Task<AuthResult> SubmitNewDeviceVerificationAsync(
        AuthenticationAttemptLease attempt,
        string otp,
        CancellationToken ct = default) =>
        Task.FromResult<AuthResult>(new AuthResult.Failure(
            "New-device verification is not available.",
            AuthFailureKind.Unspecified));
    Task<bool> ResendNewDeviceVerificationAsync(
        AuthenticationAttemptLease attempt,
        CancellationToken ct = default) => Task.FromResult(false);
    Task<AuthResult> UnlockAsync(AuthenticationAttemptLease attempt, string masterPassword, CancellationToken ct = default);
    Task<AuthResult> UnlockWithPinAsync(AuthenticationAttemptLease attempt, string pin, CancellationToken ct = default);
    Task<AuthResult> UnlockWithHelloAsync(AuthenticationAttemptLease attempt, byte[] signature, CancellationToken ct = default);
    Task<AuthResult> UnlockWithKeyConnectorAsync(AuthenticationAttemptLease attempt, CancellationToken ct = default) =>
        Task.FromResult<AuthResult>(new AuthResult.Failure(
            "Key Connector unlock is not available.",
            AuthFailureKind.UnsupportedSsoDecryption));
    Task<AuthResult> CompleteTrustedDeviceApprovalAsync(
        VaultSessionLease authenticatedSession,
        byte[] userKey,
        bool trustCurrentDevice,
        CancellationToken ct = default)
    {
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(userKey);
        return Task.FromResult<AuthResult>(new AuthResult.Failure(
            "Trusted-device approval is not available.",
            AuthFailureKind.UnsupportedSsoDecryption));
    }
    Task<AuthResult> CompleteTdeV2JitProvisioningAsync(
        VaultSessionLease authenticatedSession,
        string organizationIdentifier,
        bool trustCurrentDevice,
        CancellationToken ct = default) =>
        Task.FromResult<AuthResult>(new AuthResult.Failure(
            "V2 TDE JIT provisioning is not available.",
            AuthFailureKind.UnsupportedSsoDecryption));
    Task<AuthResult> CompleteRequiredPasswordChangeAsync(
        VaultSessionLease restrictedSession,
        string newPassword,
        string? hint,
        CancellationToken ct = default) =>
        Task.FromResult<AuthResult>(new AuthResult.Failure(
            "Required password change is not available.",
            AuthFailureKind.Unspecified));
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
    UnsupportedSsoDecryption,
    SsoAuthorization,
    SafeMessage,
    OutcomeUncertain,
    AccountDeletionRecoveryRequired,
    RateLimited,
}

public enum LoginWithDeviceUiStatus
{
    Waiting,
    Pending,
    Denied,
    Expired,
}

public enum LoginWithDeviceCancellationStatus
{
    Cancelled,
    OutcomeUncertain,
}

public sealed record LoginWithDeviceCancellationResult(
    LoginWithDeviceCancellationStatus Status);

public abstract record LoginWithDeviceQuarantineInfo
{
    public bool BlocksLoginWithDevice => this is not Absent;

    public sealed record Absent : LoginWithDeviceQuarantineInfo;

    public sealed record Found(
        Guid EntryId,
        long Revision,
        string RequestId,
        DateTimeOffset ArmedAtUtc,
        DateTimeOffset RequestExpiresAtUtc,
        DateTimeOffset ClearEligibleAtUtc,
        bool IsClearEligible) : LoginWithDeviceQuarantineInfo;

    public sealed record Corrupt : LoginWithDeviceQuarantineInfo;

    public sealed record Unavailable : LoginWithDeviceQuarantineInfo;
}

public enum LoginWithDeviceQuarantineClearStatus
{
    Cleared,
    AlreadyAbsent,
    NotEligible,
    Changed,
    Corrupt,
    Unavailable,
}

public sealed record LoginWithDeviceQuarantineClearResult(
    LoginWithDeviceQuarantineClearStatus Status)
{
    public bool IsCleared => Status is
        LoginWithDeviceQuarantineClearStatus.Cleared
        or LoginWithDeviceQuarantineClearStatus.AlreadyAbsent;
}

public abstract record AuthResult
{
    public sealed record Success(VaultSessionLease Lease) : AuthResult;
    public sealed record SsoAuthenticated(VaultSessionLease Lease) : AuthResult;
    public sealed record TrustedDeviceApprovalRequired(
        VaultSessionLease Lease,
        bool HasAdminApproval,
        bool HasLoginApprovingDevice = true) : AuthResult;
    public sealed record TdeV2JitProvisioningRequired(
        VaultSessionLease Lease,
        string OrganizationIdentifier,
        bool? RequiredTrustCurrentDevice = null) : AuthResult;
    public sealed record TwoFactorRequired(
        IReadOnlyList<int> Providers,
        IReadOnlyDictionary<int, string>? ProviderData = null) : AuthResult;
    public sealed record DeviceVerificationRequired : AuthResult;
    public sealed record LoginWithDeviceWaiting(
        string RequestId,
        string FingerprintPhrase,
        DateTimeOffset ExpiresAtUtc) : AuthResult
    {
        public LoginWithDeviceUiStatus Status => LoginWithDeviceUiStatus.Waiting;
    }
    public sealed record LoginWithDevicePending(
        string RequestId,
        string FingerprintPhrase,
        DateTimeOffset ExpiresAtUtc,
        LoginWithDeviceUiStatus Status) : AuthResult;
    public sealed record LoginWithDeviceOutcomeUncertain : AuthResult
    {
        public AuthFailureKind Kind => AuthFailureKind.OutcomeUncertain;
    }
    public sealed record PasswordChangeRequired(
        VaultSessionLease Lease,
        Core.Models.RequiredPasswordChangeReason Reason,
        Core.Models.MasterPasswordPolicyRequirements? Policy) : AuthResult;
    public sealed record PasswordChanged : AuthResult;
    public sealed record PasswordChangeUncertain : AuthResult;
    public sealed record Failure(
        string Message,
        AuthFailureKind Kind = AuthFailureKind.Unspecified) : AuthResult;
    public sealed record PinCleared(string Message) : AuthResult;
}
