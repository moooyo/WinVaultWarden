using Core.Services;
using Xunit;

namespace Vault.Tests;

public sealed class DuoNavigationPolicyTests
{
    private static readonly DuoTwoFactorChallenge Challenge = new(
        new Uri("https://api-test.duosecurity.com/oauth/v1/authorize?request=jwt"),
        new Uri("https://vault.example/duo-redirect-connector.html?client=desktop"),
        "state1234567890ABCDEF");

    [Theory]
    [InlineData("https://api-test.duosecurity.com/oauth/v1/authorize?request=jwt")]
    [InlineData("https://api-test.duosecurity.com/frame/v4/auth")]
    public void Evaluate_AllowsOnlyTheOriginalDuoOrigin(string navigationUrl)
    {
        var decision = DuoNavigationPolicy.Evaluate(Challenge, new Uri(navigationUrl));

        Assert.Equal(DuoNavigationAction.Allow, decision.Action);
        Assert.Null(decision.Token);
    }

    [Theory]
    [InlineData("https://evil.example/oauth/v1/authorize")]
    [InlineData("https://api-other.duosecurity.com/oauth/v1/authorize")]
    [InlineData("http://api-test.duosecurity.com/oauth/v1/authorize")]
    [InlineData("https://api-test.duosecurity.com:444/oauth/v1/authorize")]
    [InlineData("https://user@api-test.duosecurity.com/oauth/v1/authorize")]
    public void Evaluate_RejectsNavigationOutsideTheOriginalDuoOrigin(string navigationUrl)
    {
        var decision = DuoNavigationPolicy.Evaluate(Challenge, new Uri(navigationUrl));

        Assert.Equal(DuoNavigationAction.Reject, decision.Action);
        Assert.Null(decision.Token);
    }

    [Fact]
    public void Evaluate_CompletesOnlyAnExactValidCallback()
    {
        var decision = DuoNavigationPolicy.Evaluate(
            Challenge,
            new Uri(
                "https://vault.example/duo-redirect-connector.html"
                + "?client=desktop&code=duo-code&state=state1234567890ABCDEF"));

        Assert.Equal(DuoNavigationAction.Complete, decision.Action);
        Assert.Equal("duo-code|state1234567890ABCDEF", decision.Token);
    }

    [Theory]
    [InlineData("https://vault.example/duo-redirect-connector.html?client=desktop&code=duo-code&state=wrong")]
    [InlineData("https://vault.example/duo-redirect-connector.html?client=desktop&state=state1234567890ABCDEF")]
    [InlineData("https://vault.example/duo-redirect-connector.html?client=desktop&code=duo-code&state=state1234567890ABCDEF&extra=1")]
    [InlineData("https://vault.example/duo-redirect-connector.html?client=desktop&code=duo-code&state=state1234567890ABCDEF#fragment")]
    [InlineData("https://user@vault.example/duo-redirect-connector.html?client=desktop&code=duo-code&state=state1234567890ABCDEF")]
    [InlineData("https://vault.example/not-the-connector?client=desktop&code=duo-code&state=state1234567890ABCDEF")]
    public void Evaluate_RejectsMalformedOrInexactCallbacks(string navigationUrl)
    {
        var decision = DuoNavigationPolicy.Evaluate(Challenge, new Uri(navigationUrl));

        Assert.Equal(DuoNavigationAction.Reject, decision.Action);
        Assert.Null(decision.Token);
    }
}
