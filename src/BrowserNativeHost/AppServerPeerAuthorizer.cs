using Core.BrowserIntegration;

namespace BrowserNativeHost;

/// <summary>
/// Native-host authorizer for the passkey pipe (Task 3): verifies the App server (or, symmetrically,
/// a connecting client) using the real Windows identity reader. Pure decision lives in
/// <see cref="PeerIdentityPolicy"/>; the peer PID always comes from the pipe kernel object.
/// </summary>
public sealed class AppServerPeerAuthorizer : PipePeerAuthorizer
{
    public AppServerPeerAuthorizer()
        : base(new WindowsProcessIdentityReader())
    {
    }

    public AppServerPeerAuthorizer(IProcessIdentityReader reader)
        : base(reader)
    {
    }
}
