namespace Core.Services;

public interface IRegisterService
{
    Task<RegistrationOutcome> RegisterAsync(
        string serverUrl,
        string email,
        string? name,
        string password,
        string? hint,
        CancellationToken ct = default);
}

public abstract record RegistrationOutcome
{
    public sealed record Registered : RegistrationOutcome;

    public sealed record EmailVerificationRequired : RegistrationOutcome;

    public sealed record OutcomeUncertain : RegistrationOutcome;
}
