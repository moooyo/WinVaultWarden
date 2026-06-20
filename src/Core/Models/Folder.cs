namespace Core.Models;

public sealed class Folder
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset RevisionDate { get; init; }
}
