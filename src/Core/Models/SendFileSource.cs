namespace Core.Models;

/// <summary>
/// Backward-compatible Send-specific file source. The shared streaming crypto layer consumes the
/// <see cref="ReopenableFileSource"/> base contract directly.
/// </summary>
public sealed record SendFileSource(
    string FileName,
    long Length,
    Func<CancellationToken, ValueTask<Stream>> OpenReadAsync)
    : ReopenableFileSource(FileName, Length, OpenReadAsync)
{
    // Bitwarden clients cap Send plaintext at 500 MiB. Vaultwarden reserves a separate 525 MB
    // encrypted-body allowance in src/api/core/sends.rs.
    public const long MaxPlaintextBytes = 500L * 1024 * 1024;

    public static void ValidateLength(long length)
    {
        ReopenableFileSource.ValidateLength(length, MaxPlaintextBytes);
    }
}
