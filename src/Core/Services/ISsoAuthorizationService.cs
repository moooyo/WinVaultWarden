using Core.Models;

namespace Core.Services;

public interface ISsoAuthorizationService
{
    Task<SsoAuthorizationCode> AuthorizeAsync(
        ServerEndpoints endpoints,
        string email,
        CancellationToken ct = default);

    Task<SsoAuthorizationCode> AuthorizeAsync(
        ServerEndpoints endpoints,
        string email,
        string? organizationIdentifier,
        IProgress<bool>? browserOpened,
        CancellationToken ct = default) => AuthorizeAsync(endpoints, email, ct);

    Task<bool> ReopenPendingBrowserAsync(CancellationToken ct = default) => Task.FromResult(false);
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
