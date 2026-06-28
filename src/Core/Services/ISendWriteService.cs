using Core.Models;

namespace Core.Services;

public interface ISendWriteService
{
    Task SaveTextSendAsync(SendDraftModel draft, CancellationToken ct = default);
    Task SaveFileSendAsync(SendDraftModel draft, byte[] fileBytes, CancellationToken ct = default);
    Task DeleteSendAsync(string sendId, CancellationToken ct = default);
    Task<Core.Models.Send> RemovePasswordAsync(string sendId, CancellationToken ct = default);
}
