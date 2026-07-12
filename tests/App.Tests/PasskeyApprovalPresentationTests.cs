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

    // ── AOT / WinUI ItemsSource contract tests ──────────────────────────────────────────────────
    // Under Native AOT the WinUI x:Bind-generated setter (Update_Presentation_Candidates) assigns
    // Candidates directly to ComboBox.ItemsSource.  The WinRT marshalling layer can only project a
    // concrete T[] (or ObservableCollection<T>) without a runtime interface-wrapper step.  An
    // IReadOnlyList<T> backed by a non-array type (e.g. List<T>) causes E_INVALIDARG / "Value does
    // not fall within the expected range" on the first ItemsControl.set_ItemsSource call.

    [Fact]
    public void Candidates_DeclaredPropertyType_IsConcreteArray_NotIReadOnlyList()
    {
        // The static property type must be PasskeyCredentialTarget[] — not the interface —
        // so the compiler-generated x:Bind setter is typed correctly and AOT trimming can
        // emit the WinRT projection for the concrete array rather than the interface.
        var prop = typeof(PasskeyApprovalPresentation)
            .GetProperty(nameof(PasskeyApprovalPresentation.Candidates));
        Assert.NotNull(prop);
        Assert.Equal(typeof(PasskeyCredentialTarget[]), prop!.PropertyType);
    }

    [Fact]
    public void Build_MaterializesCandidatesAsArray_WhenInputIsArbitraryIReadOnlyList()
    {
        // Explicitly supply a List<T> (not a C# collection expression that the compiler might
        // optimise into an array) to prove Build converts whatever IReadOnlyList it receives.
        // Before the fix Build stores the argument directly → runtime type is List<T>, which
        // fails WinUI's ItemsSource cast under Native AOT.
        IReadOnlyList<PasskeyCredentialTarget> input = new List<PasskeyCredentialTarget>
        {
            new("c1", "cred1", "Account A", "alice", "Alice", false),
            new("c2", "cred2", "Account B", "bob", "Bob", false),
        };

        var presentation = PasskeyApprovalPresentation.Build(
            new PasskeyApprovalSource("https://a.com", "https://a.com", "a.com", "a.com", false),
            reportedRpName: null,
            candidates: input);

        Assert.IsType<PasskeyCredentialTarget[]>(presentation.Candidates);
        // Multiple candidates → no pre-selection.
        Assert.Null(presentation.InitialSelection);
    }

    [Fact]
    public void Build_SingleCandidate_InitialSelection_IsReferenceEqualToElementInMaterialisedCandidates()
    {
        // After materialisation, InitialSelection must be the same object instance as Candidates[0]
        // (both sourced from the same ToArray() result), not a separate reference to the original
        // input element.
        IReadOnlyList<PasskeyCredentialTarget> input = new List<PasskeyCredentialTarget>
        {
            new("c1", "cred1", "Acme", "user", "User", false),
        };

        var presentation = PasskeyApprovalPresentation.Build(
            new PasskeyApprovalSource("https://acme.com", "https://acme.com", "acme.com", "acme.com", false),
            reportedRpName: null,
            candidates: input);

        Assert.IsType<PasskeyCredentialTarget[]>(presentation.Candidates);
        Assert.True(
            ReferenceEquals(presentation.InitialSelection, presentation.Candidates[0]),
            "InitialSelection must be the element from the materialised Candidates array.");
    }
}
