namespace Core.Services;

public interface ISendService
{
    Task<IReadOnlyList<Core.Models.Send>> GetSendsAsync(CancellationToken ct = default);
}
