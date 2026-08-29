using Api.Dtos;

namespace Api;

public interface IRegistrationApiClient : IAnonymousApiRole
{
    Task<RegistrationVerificationResult> RequestRegistrationVerificationTokenAsync(
        RegisterVerificationRequest request,
        CancellationToken ct = default);

    Task<RegistrationWriteResult> FinishRegistrationAsync(
        RegisterFinishRequest request,
        CancellationToken ct = default);

    // Legacy Vaultwarden endpoint retained for servers that do not expose register/finish.
    Task<RegistrationWriteResult> RegisterAsync(
        RegisterRequest request,
        CancellationToken ct = default);
}

public abstract record RegistrationVerificationResult
{
    public sealed record InlineToken(string Token) : RegistrationVerificationResult;

    public sealed record EmailVerificationSent : RegistrationVerificationResult;
}

public abstract record RegistrationWriteResult
{
    public sealed record Completed : RegistrationWriteResult;

    public sealed record OutcomeUncertain : RegistrationWriteResult;
}
