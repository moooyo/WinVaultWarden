using Crypto.PasswordStrength;
using Xunit;

namespace Crypto.Tests;

public class PatternMatcherTests
{
    private readonly Omnimatch _omni = new(new DictionaryMatcher(FrequencyDictionaries.Load()));

    [Fact] public void Repeat_Detected() =>
        Assert.Contains(_omni.Match("aaaaaa"), m => m.Type == StrengthMatchType.Repeat);

    [Fact] public void Sequence_Detected() =>
        Assert.Contains(_omni.Match("abcdef"), m => m.Type == StrengthMatchType.Sequence);

    [Fact] public void DigitSequence_Detected() =>
        Assert.Contains(_omni.Match("123456"), m => m.Type == StrengthMatchType.Sequence);

    [Fact] public void Spatial_Detected() =>
        Assert.Contains(_omni.Match("qwerty"), m => m.Type == StrengthMatchType.Spatial);

    [Fact] public void Date_Detected() =>
        Assert.Contains(_omni.Match("2010"), m => m.Type == StrengthMatchType.Date || m.Type == StrengthMatchType.Sequence || m.Type == StrengthMatchType.Dictionary || m.Type == StrengthMatchType.Bruteforce);

    [Fact] public void Bruteforce_AlwaysPresent() =>
        Assert.Contains(_omni.Match("x7q2zk9w"), m => m.Type == StrengthMatchType.Bruteforce);

    [Theory]
    [InlineData("abcabcabc", 0, 8)]
    [InlineData("xxabcabcabczz", 2, 10)]
    [InlineData("ab\nab\nab\n", 0, 8)]
    public void RepeatedUnit_HasExactSpan(string password, int start, int end) =>
        Assert.Contains(_omni.Match(password), match => match.Type == StrengthMatchType.Repeat
            && match.I == start && match.J == end
            && match.Token == password.Substring(start, end - start + 1));

    [Fact]
    public void OversizedPatternInput_FailsBeforeRegexOrPatternSearch() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _omni.Match(new string('1', PasswordStrengthEvaluator.MaximumAnalyzedLength + 1)));
}
