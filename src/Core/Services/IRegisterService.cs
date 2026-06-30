namespace Core.Services;

public interface IRegisterService
{
    Task RegisterAsync(string serverUrl, string email, string? name, string password, string? hint, CancellationToken ct = default);
}
