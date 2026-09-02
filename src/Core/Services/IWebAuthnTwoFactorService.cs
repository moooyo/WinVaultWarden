using Core.Models;

namespace Core.Services;

public interface IWebAuthnTwoFactorService
{
    bool IsSupported { get; }

    Task<string> AuthenticateAsync(
        string providerDataJson,
        ServerEndpoints endpoints,
        CancellationToken ct = default);
}

public class WebAuthnTwoFactorException : Exception
{
    public WebAuthnTwoFactorException(string message)
        : base(message)
    {
    }

    public WebAuthnTwoFactorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class WebAuthnTwoFactorCancelledException : WebAuthnTwoFactorException
{
    public WebAuthnTwoFactorCancelledException()
        : base("The WebAuthn operation was cancelled by the user.")
    {
    }
}
