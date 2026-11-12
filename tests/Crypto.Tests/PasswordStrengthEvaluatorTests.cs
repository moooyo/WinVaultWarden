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

    [Fact]
    public void UppercaseInDictionaryWord_IncreasesGuesses()
    {
        // R19:大写乘子必须真正生效——用原文子串判断而非已小写化的 m.Token(此前恒 false 是死代码)。
        var lower = _e.Evaluate("password").Guesses;
        var upper = _e.Evaluate("Password").Guesses;
        Assert.True(upper > lower, $"含大写应提升猜测量: upper={upper} lower={lower}");
    }

    [Theory]
    [InlineData(257)]
    [InlineData(4096)]
    [InlineData(1_000_000)]
    public void OversizedInput_ReturnsIncompleteConservativeResult(int length)
    {
        var result = _e.Evaluate(new string('x', length));

        Assert.False(result.IsComplete);
        Assert.Equal(0, result.Score);
        Assert.Equal(0, result.Guesses);
    }

    [Fact]
    public void OversizedInput_DoesNotScoreAnApparentlyStrongPrefix()
    {
        const string prefix = "9xK#mq2Lp@7vWn4Tz!Rb";
        var result = _e.Evaluate(prefix + new string('a', PasswordStrengthEvaluator.MaximumAnalyzedLength));

        Assert.False(result.IsComplete);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void MaximumProtocolMasterPassword_IsFullyAnalyzed()
    {
        var result = _e.Evaluate(new string('a', 128));

        Assert.True(result.IsComplete);
        Assert.True(result.Score <= 2);
    }

    [Fact]
    public void AnalysisBoundary_IsInclusive()
    {
        Assert.True(_e.Evaluate(new string('a', PasswordStrengthEvaluator.MaximumAnalyzedLength)).IsComplete);
        Assert.False(_e.Evaluate(new string('a', PasswordStrengthEvaluator.MaximumAnalyzedLength + 1)).IsComplete);
    }

    [Fact]
    public void OversizedInput_DoesNotAllocateSubstringsOrInputSizedBuffers()
    {
        var password = new string('1', 1_000_000);
        _e.Evaluate(password);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var result = _e.Evaluate(password);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(result.IsComplete);
        Assert.InRange(allocated, 0, 1024);
    }
}
