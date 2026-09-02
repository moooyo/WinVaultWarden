using Core.Models;
using Core.Session;
using Xunit;

namespace Vault.Tests;

public sealed class SessionTerminationSignalTests
{
    [Fact]
    public void Request_ForwardsPinPolicyFailureNoticeUnchanged()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            ServerEndpoints.ForSelfHosted("https://vault.example"));
        var notice = new SessionTerminationNotice(
            SessionTerminationReason.PinPolicyEnforcementFailed,
            lease);
        var signal = new SessionTerminationSignal();
        SessionTerminationNotice? observed = null;
        signal.TerminationRequested += value => observed = value;

        signal.Request(notice);

        Assert.Same(notice, observed);
    }
}
