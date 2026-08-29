using Core.Models;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage;

namespace App.Services;

/// <summary>
/// Adapts a StorageFile to a reopenable source without loading its contents into managed memory.
/// </summary>
public static class StorageFileSource
{
    public static async Task<ReopenableFileSource> CreateAsync(
        StorageFile file,
        long maxPlaintextBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var properties = await file.GetBasicPropertiesAsync().AsTask(ct);
        var length = checked((long)properties.Size);
        ReopenableFileSource.ValidateLength(length, maxPlaintextBytes);
        return new ReopenableFileSource(
            file.Name,
            length,
            token => OpenAsync(file, token));
    }

    private static async ValueTask<Stream> OpenAsync(StorageFile file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var openTask = file.OpenStreamForReadAsync();
        return await CancelWinsTask.AwaitAsync(
            openTask,
            static stream => stream.DisposeAsync(),
            ct).ConfigureAwait(false);
    }
}
