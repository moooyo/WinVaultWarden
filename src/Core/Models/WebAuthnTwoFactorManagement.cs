namespace Core.Models;

public sealed record WebAuthnTwoFactorCredential(
    int Id,
    string Name,
    bool Migrated);

public sealed record WebAuthnTwoFactorDetails
{
    public WebAuthnTwoFactorDetails(
        bool enabled,
        IReadOnlyList<WebAuthnTwoFactorCredential> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        Enabled = enabled;
        Credentials = Array.AsReadOnly(credentials.ToArray());
    }

    public bool Enabled { get; }

    public IReadOnlyList<WebAuthnTwoFactorCredential> Credentials { get; }
}

public abstract record WebAuthnRegistrationOutcome
{
    private WebAuthnRegistrationOutcome()
    {
    }

    public sealed record Confirmed(
        WebAuthnTwoFactorDetails Details,
        bool Reconciled) : WebAuthnRegistrationOutcome;

    public sealed record OutcomeUncertain(
        WebAuthnTwoFactorDetails? ObservedDetails = null) : WebAuthnRegistrationOutcome;
}
