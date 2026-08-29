namespace Core.Models;

/// <summary>
/// Describes the independently hosted service endpoints used by one server environment.
/// </summary>
public sealed record ServerEndpoints
{
    public ServerEndpoints(
        ServerEndpoint webVault,
        ServerEndpoint api,
        ServerEndpoint identity,
        ServerEndpoint notifications,
        ServerEndpoint icons,
        ServerEndpoint sendWeb)
    {
        WebVault = RequireInitialized(webVault, nameof(webVault));
        Api = RequireInitialized(api, nameof(api));
        Identity = RequireInitialized(identity, nameof(identity));
        Notifications = RequireInitialized(notifications, nameof(notifications));
        Icons = RequireInitialized(icons, nameof(icons));
        SendWeb = RequireInitialized(sendWeb, nameof(sendWeb));
    }

    public ServerEndpoint WebVault { get; }

    public ServerEndpoint Api { get; }

    public ServerEndpoint Identity { get; }

    public ServerEndpoint Notifications { get; }

    public ServerEndpoint Icons { get; }

    public ServerEndpoint SendWeb { get; }

    public static ServerEndpoints BitwardenUs { get; } = new(
        new ServerEndpoint("https://vault.bitwarden.com"),
        new ServerEndpoint("https://api.bitwarden.com"),
        new ServerEndpoint("https://identity.bitwarden.com"),
        new ServerEndpoint("https://notifications.bitwarden.com"),
        new ServerEndpoint("https://icons.bitwarden.net"),
        new ServerEndpoint("https://send.bitwarden.com"));

    public static ServerEndpoints BitwardenEu { get; } = new(
        new ServerEndpoint("https://vault.bitwarden.eu"),
        new ServerEndpoint("https://api.bitwarden.eu"),
        new ServerEndpoint("https://identity.bitwarden.eu"),
        new ServerEndpoint("https://notifications.bitwarden.eu"),
        new ServerEndpoint("https://icons.bitwarden.eu"),
        new ServerEndpoint("https://vault.bitwarden.eu"));

    public static ServerEndpoints ForSelfHosted(string webVault) =>
        ForSelfHosted(new ServerEndpoint(webVault));

    public static ServerEndpoints ForSelfHosted(ServerEndpoint webVault)
    {
        RequireInitialized(webVault, nameof(webVault));

        return new ServerEndpoints(
            webVault,
            webVault.Append("api"),
            webVault.Append("identity"),
            webVault.Append("notifications"),
            webVault.Append("icons"),
            webVault);
    }

    public static ServerEndpoints FromWebVault(string webVault) =>
        FromWebVault(new ServerEndpoint(webVault));

    public static ServerEndpoints FromWebVault(ServerEndpoint webVault)
    {
        RequireInitialized(webVault, nameof(webVault));

        if (webVault == BitwardenUs.WebVault || webVault == BitwardenUs.SendWeb)
            return BitwardenUs;

        if (webVault == BitwardenEu.WebVault || webVault == BitwardenEu.SendWeb)
            return BitwardenEu;

        return ForSelfHosted(webVault);
    }

    private static ServerEndpoint RequireInitialized(ServerEndpoint endpoint, string parameterName)
    {
        if (!endpoint.IsInitialized)
            throw new ArgumentException("Server endpoint must be initialized.", parameterName);

        return endpoint;
    }
}
