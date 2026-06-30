using Crypto.PasswordStrength;
using Xunit;

namespace Crypto.Tests;

public class PasswordStrengthEvaluatorTests
{
    private readonly PasswordStrengthEvaluator _e = new(new Omnimatch(new DictionaryMatcher(FrequencyDictionaries.Load())));

    [Theory]
    [InlineData("123456")]
    [InlineData("password")]
    [InlineData("qwerty")]
    [InlineData("abcdef")]
    [InlineData("aaaaaa")]
    [InlineData("letmein")]
    [InlineData("p@ssw0rd")]
    [InlineData("11111111")]
    public void WeakPasswords_ScoreAtMost2(string pw) =>
        Assert.True(_e.Evaluate(pw).Score <= 2, $"{pw} -> {_e.Evaluate(pw).Score}");

    [Theory]
    [InlineData("Tr0ub4dour&3xpling-Z9q")]
    [InlineData("9xK#mq2Lp@7vWn4Tz!Rb")]
    [InlineData("correct-horse-battery-staple-99X")]
    public void StrongPasswords_ScoreAtLeast3(string pw) =>
        Assert.True(_e.Evaluate(pw).Score >= 3, $"{pw} -> {_e.Evaluate(pw).Score}");

    [Fact]
    public void Empty_ScoresZero() => Assert.Equal(0, _e.Evaluate("").Score);
}
