using Core.BrowserIntegration;

namespace App.Services;

/// <summary>
/// App-side authorizer for the passkey pipe (Task 3): the App is the pipe server, so it verifies
/// the connecting native-host client via the real Windows identity reader. The pure decision lives
/// in <see cref="PeerIdentityPolicy"/>; the peer PID always comes from the pipe kernel object, never
/// from a self-reported hello.
/// </summary>
public sealed class ClientPeerAuthorizer : PipePeerAuthorizer
{
    public ClientPeerAuthorizer(IProcessIdentityReader reader)
        : base(reader)
    {
    }
}
