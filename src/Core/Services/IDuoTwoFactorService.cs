using Core.Models;

namespace Core.Services;

public interface IDuoTwoFactorService
{
    bool IsSupported { get; }

    bool CanAuthenticate(string providerDataJson, ServerEndpoints endpoints);

    Task<string> AuthenticateAsync(
        string providerDataJson,
        ServerEndpoints endpoints,
        CancellationToken ct = default);
}

public class DuoTwoFactorException : Exception
{
    public DuoTwoFactorException(string message)
        : base(message)
    {
    }

    public DuoTwoFactorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class DuoLegacyUnsupportedException : DuoTwoFactorException
{
    public DuoLegacyUnsupportedException()
        : base("Legacy Duo iframe authentication is not supported.")
    {
    }
}

public sealed class DuoTwoFactorCancelledException : DuoTwoFactorException
{
    public DuoTwoFactorCancelledException()
        : base("The Duo operation was cancelled by the user.")
    {
    }
}
