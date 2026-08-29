namespace Core.Models;

/// <summary>
/// Prepared streaming upload content. <see cref="Length"/> is the exact encoded length used for
/// Content-Length validation. Each write reopens the plaintext source and never buffers the whole
/// plaintext or ciphertext in memory.
/// </summary>
public sealed record StreamUploadContent(
    long Length,
    Func<Stream, CancellationToken, Task> WriteAsync);
