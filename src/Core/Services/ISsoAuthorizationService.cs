using Core.Models;

namespace Core.Services;

public interface ISsoAuthorizationService
{
    Task<SsoAuthorizationCode> AuthorizeAsync(
        ServerEndpoints endpoints,
        string email,
        CancellationToken ct = default);
}

public sealed record SsoAuthorizationCode(
    string Code,
    string CodeVerifier,
    string RedirectUri,
    string State,
    string? OrganizationIdentifier);

public sealed class SsoAuthorizationException : Exception
{
    public SsoAuthorizationException(string message)
        : base(message)
    {
    }

    public SsoAuthorizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
