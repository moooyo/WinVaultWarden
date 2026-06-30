using Crypto.PasswordStrength;
using Xunit;

namespace Crypto.Tests;

public class DictionaryMatcherTests
{
    private readonly DictionaryMatcher _m = new(FrequencyDictionaries.Load());

    [Fact]
    public void Matches_CommonPassword_WithLowRank()
    {
        var matches = _m.Match("password");
        Assert.Contains(matches, x => x.Token == "password" && x.Type == StrengthMatchType.Dictionary && x.Rank > 0);
    }

    [Fact]
    public void Matches_Reversed()
    {
        var matches = _m.Match("drowssap"); // "password" reversed
        Assert.Contains(matches, x => x.Reversed && x.Token == "drowssap");
    }

    [Fact]
    public void Matches_L33t()
    {
        var matches = _m.Match("p@ssw0rd"); // l33t of "password"
        Assert.Contains(matches, x => x.L33t && x.Type == StrengthMatchType.Dictionary);
    }

    [Fact]
    public void NoMatch_ForRandomString()
    {
        var matches = _m.Match("x7q2zk9w");
        Assert.DoesNotContain(matches, x => x.Type == StrengthMatchType.Dictionary && x.Token.Length >= 4);
    }
}
