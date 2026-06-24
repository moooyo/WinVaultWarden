namespace Core.Models;

public sealed record AccountInfo(string Email, string ServerUrl, string Initial, string KdfSummary)
{
    public static AccountInfo Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);
}
