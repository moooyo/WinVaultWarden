namespace Core.Models;

public sealed class Send
{
    public string Id { get; init; } = string.Empty;
    public int Type { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset? DeletionDate { get; init; }
}
