using Core.Models;

namespace Core.Services;

public interface ISendAccessService
{
    Task<SendAccessResult> AccessAsync(string shareUrl, string? password, CancellationToken ct = default);
    Task<byte[]> DownloadFileAsync(SendAccessResult accessed, CancellationToken ct = default);
}
