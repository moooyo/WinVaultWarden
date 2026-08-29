namespace Core.Models;

/// <summary>
/// Describes a file whose content can be reopened from the beginning. Streaming encryption opens
/// the source once to authenticate the ciphertext and again to write the authenticated payload,
/// without retaining the complete plaintext or ciphertext in memory.
/// </summary>
public record ReopenableFileSource(
    string FileName,
    long Length,
    Func<CancellationToken, ValueTask<Stream>> OpenReadAsync)
{
    public static void ValidateLength(long length, long maxPlaintextBytes)
    {
        if (maxPlaintextBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maxPlaintextBytes));
        if (length < 0 || length > maxPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                $"File must be between 0 and {maxPlaintextBytes} bytes.");
        }
    }
}
