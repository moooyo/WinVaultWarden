using Core.Models;

namespace Core.Services;

public interface ISendAccessService
{
    Task<SendAccessResult> AccessAsync(string shareUrl, string? password, CancellationToken ct = default);
    Task<long> DownloadFileAsync(
        SendDownloadLease download,
        Stream plaintextDestination,
        CancellationToken ct = default);
}
