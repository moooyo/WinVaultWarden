using App.Services;
using Core.Services;
using Xunit;

namespace App.Tests;

public class PasskeyApprovalPresentationTests
{
    [Fact]
    public void Build_ExposesUnicodePunycodeTopAndCrossOrigin()
    {
        var source = new PasskeyApprovalSource(
            "https://login.xn--mnchen-3ya.de",
            "https://xn--mnchen-3ya.de",
            "xn--mnchen-3ya.de",
            "münchen.de",
            true);
        var presentation = PasskeyApprovalPresentation.Build(
            source,
            reportedRpName: "München Login",
            candidates:
            [
                new("cipher-a", "credential-a", "Account A", "alice", "Alice", false),
                new("cipher-b", "credential-b", "Account B", "bob", "Bob", true),
            ]);

        Assert.Equal("https://login.münchen.de", presentation.FrameOriginUnicode);
        Assert.Equal("https://login.xn--mnchen-3ya.de", presentation.FrameOriginAscii);
        Assert.Equal("https://münchen.de", presentation.TopOriginUnicode);
        Assert.Equal("https://xn--mnchen-3ya.de", presentation.TopOriginAscii);
        Assert.Equal("xn--mnchen-3ya.de", presentation.RpIdAscii);
        Assert.Equal("münchen.de", presentation.RpIdUnicode);
        Assert.True(presentation.IsCrossOrigin);
        Assert.Null(presentation.InitialSelection);
        Assert.Equal("网站自报名称：München Login", presentation.ReportedName);
    }

    [Fact]
    public void Build_SingleCandidate_PreselectsItAndOmitsBlankReportedName()
    {
        var only = new PasskeyCredentialTarget("cipher-a", "credential-a", "Account A", "alice", "Alice", false);
        var presentation = PasskeyApprovalPresentation.Build(
            new PasskeyApprovalSource("https://github.com", "https://github.com", "github.com", "github.com", false),
            reportedRpName: "   ",
            candidates: [only]);

        Assert.Same(only, presentation.InitialSelection);
        Assert.Null(presentation.ReportedName);
        Assert.False(presentation.IsCrossOrigin);
        Assert.Equal("https://github.com", presentation.TopOriginUnicode);
        Assert.Same(only, Assert.Single(presentation.Candidates));
    }

    [Fact]
    public void CredentialTarget_DisplayName_JoinsDistinctNonEmptyParts()
    {
        Assert.Equal(
            "Alice Smith · alice@example.com · GitHub",
            new PasskeyCredentialTarget("c", "cred", "GitHub", "alice@example.com", "Alice Smith", false).DisplayName);
        Assert.Equal(
            "GitHub",
            new PasskeyCredentialTarget("c", "cred", "GitHub", null, null, false).DisplayName);
        // Case-insensitive de-dup keeps the display-name casing and drops the duplicate username.
        Assert.Equal(
            "Alice · Account A",
            new PasskeyCredentialTarget("c", "cred", "Account A", "alice", "Alice", false).DisplayName);
    }
}
