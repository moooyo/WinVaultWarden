using Core.Models;
using Windows.Storage;

namespace App.Services;

// Retains the Send-specific model while sharing the StorageFile streaming adapter with attachments.
public static class StorageFileSendSource
{
    public static async Task<SendFileSource> CreateAsync(StorageFile file, CancellationToken ct = default)
    {
        var source = await StorageFileSource.CreateAsync(file, SendFileSource.MaxPlaintextBytes, ct);
        return new SendFileSource(
            source.FileName,
            source.Length,
            source.OpenReadAsync);
    }
}
