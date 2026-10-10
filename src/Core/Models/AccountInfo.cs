namespace Core.Models;

public sealed record AccountInfo(
    string Id,
    string Email,
    ServerEndpoints? Endpoints,
    string Initial,
    string KdfSummary)
{
    public string Name { get; init; } = string.Empty;

    public bool? UserIsClaimedByOrganization { get; init; }

    public AccountInfo(
        string id,
        string email,
        ServerOrigin? origin,
        string initial,
        string kdfSummary)
        : this(
            id,
            email,
            origin is null ? null : ServerEndpoints.ForSelfHosted(origin.Value.Value),
            initial,
            kdfSummary)
    {
    }

    public ServerOrigin? Origin => Endpoints?.WebVault.Origin;
    public string ServerUrl => Endpoints?.WebVault.Value ?? string.Empty;

    public static AccountInfo Empty { get; } =
        new(string.Empty, string.Empty, (ServerEndpoints?)null, string.Empty, string.Empty);
}
