namespace Core.Models;

public sealed record DeviceInfo(
    string Id,
    string? Name,
    int Type,
    string? Identifier,
    DateTimeOffset? CreationDate,
    bool IsTrusted);
