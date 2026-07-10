namespace Core.Models;

public sealed record AccountInfo(
    string Id,
    string Email,
    ServerOrigin? Origin,
    string Initial,
    string KdfSummary)
{
    public string ServerUrl => Origin?.Value ?? string.Empty;

    public static AccountInfo Empty { get; } =
        new(string.Empty, string.Empty, null, string.Empty, string.Empty);
}
