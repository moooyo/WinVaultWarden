using System.Security.Cryptography;
using Core.Services;

namespace App.Services;

/// <summary>Reads a parser input within a byte budget and clears all owned staging buffers.</summary>
public static class BoundedImportReader
{
    public static async Task<byte[]> ReadAsync(
        Stream source,
        CancellationToken ct,
        ulong maximumBytes = VaultPortingLimits.MaximumFileSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The import source must be readable.", nameof(source));
        if (maximumBytes == 0 || maximumBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        ct.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                if ((ulong)buffer.Length + (ulong)read > maximumBytes)
                    throw new InvalidDataException("The selected backup exceeds the import size limit.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            return buffer.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chunk);
            CryptographicOperations.ZeroMemory(buffer.GetBuffer());
        }
    }
}
