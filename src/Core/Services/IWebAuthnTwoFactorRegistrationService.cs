using Core.Models;

namespace Core.Services;

/// <summary>
/// Performs the platform-authenticator ceremony for an account WebAuthn two-factor credential.
/// This is deliberately separate from login assertions and vault-stored website passkeys.
/// </summary>
public interface IWebAuthnTwoFactorRegistrationService
{
    bool IsSupported { get; }

    Task<WebAuthnRegistrationDeviceResponse> CreateCredentialAsync(
        WebAuthnRegistrationChallenge challenge,
        CancellationToken ct = default);
}
