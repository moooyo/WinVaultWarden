namespace Core.Services;

public enum DuoNavigationAction
{
    Allow,
    Complete,
    Reject,
}

public sealed record DuoNavigationDecision(
    DuoNavigationAction Action,
    string? Token = null);

public static class DuoNavigationPolicy
{
    public static DuoNavigationDecision Evaluate(
        DuoTwoFactorChallenge challenge,
        Uri navigationUri)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(navigationUri);

        if (IsCallbackEndpoint(challenge, navigationUri))
        {
            return DuoTwoFactorProtocol.TryCreateToken(challenge, navigationUri, out var token)
                ? new DuoNavigationDecision(DuoNavigationAction.Complete, token)
                : new DuoNavigationDecision(DuoNavigationAction.Reject);
        }

        return IsAllowedDuoOrigin(challenge.AuthorizationUri, navigationUri)
            ? new DuoNavigationDecision(DuoNavigationAction.Allow)
            : new DuoNavigationDecision(DuoNavigationAction.Reject);
    }

    private static bool IsCallbackEndpoint(
        DuoTwoFactorChallenge challenge,
        Uri navigationUri) =>
        SameOrigin(challenge.CallbackUri, navigationUri)
        && string.Equals(
            challenge.CallbackUri.AbsolutePath,
            navigationUri.AbsolutePath,
            StringComparison.Ordinal);

    private static bool IsAllowedDuoOrigin(Uri expectedOrigin, Uri navigationUri) =>
        navigationUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && navigationUri.IsDefaultPort
        && string.IsNullOrEmpty(navigationUri.UserInfo)
        && SameOrigin(expectedOrigin, navigationUri);

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
        && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;
}
