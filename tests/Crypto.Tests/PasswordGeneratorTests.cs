using Crypto;
using Xunit;

namespace Crypto.Tests;

[Trait("Category", "Crypto")]
public class PasswordGeneratorTests
{
    private const string AmbiguousCharacters = "IOlo01";
    private const string SpecialCharacters = "!@#$%^&*";

    [Fact]
    public void Generate_DefaultOptions_ReturnsFourteenCharacters()
    {
        var password = PasswordGenerator.Generate();

        Assert.Equal(14, password.Length);
        Assert.Contains(password, char.IsUpper);
        Assert.Contains(password, char.IsLower);
        Assert.Contains(password, char.IsDigit);
    }

    [Fact]
    public void Generate_WithoutNumbersAndSpecials_ExcludesThoseCharacters()
    {
        var password = PasswordGenerator.Generate(new PasswordGenerationOptions(
            Length: 64,
            IncludeUppercase: true,
            IncludeLowercase: true,
            IncludeNumbers: false,
            IncludeSpecial: false,
            MinUppercase: 1,
            MinLowercase: 1,
            MinNumbers: 0,
            MinSpecial: 0));

        Assert.DoesNotContain(password, c => char.IsDigit(c) || SpecialCharacters.Contains(c));
    }

    [Fact]
    public void Generate_MinNumbers_EnsuresAtLeastRequestedCount()
    {
        var password = PasswordGenerator.Generate(new PasswordGenerationOptions(
            Length: 24,
            IncludeUppercase: true,
            IncludeLowercase: true,
            IncludeNumbers: true,
            IncludeSpecial: false,
            MinUppercase: 1,
            MinLowercase: 1,
            MinNumbers: 3,
            MinSpecial: 0));

        Assert.True(password.Count(char.IsDigit) >= 3);
    }

    [Fact]
    public void Generate_AvoidAmbiguous_ExcludesAmbiguousCharacters()
    {
        var password = PasswordGenerator.Generate(new PasswordGenerationOptions(
            Length: 80,
            IncludeUppercase: true,
            IncludeLowercase: true,
            IncludeNumbers: true,
            IncludeSpecial: true,
            MinUppercase: 1,
            MinLowercase: 1,
            MinNumbers: 1,
            MinSpecial: 1,
            AvoidAmbiguous: true));

        Assert.DoesNotContain(password, c => AmbiguousCharacters.Contains(c));
    }

    [Fact]
    public void Generate_NoCharacterSetSelected_FallsBackToLowercase()
    {
        var password = PasswordGenerator.Generate(new PasswordGenerationOptions(
            Length: 16,
            IncludeUppercase: false,
            IncludeLowercase: false,
            IncludeNumbers: false,
            IncludeSpecial: false,
            MinUppercase: 0,
            MinLowercase: 0,
            MinNumbers: 0,
            MinSpecial: 0));

        Assert.Equal(16, password.Length);
        Assert.All(password, c => Assert.InRange(c, 'a', 'z'));
    }

    [Fact]
    public void Generate_MultipleCalls_AreNotAllEqual()
    {
        var values = Enumerable.Range(0, 8)
            .Select(_ => PasswordGenerator.Generate(new PasswordGenerationOptions(Length: 24)))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(values.Count > 1);
    }

    [Fact]
    public void Generate_MinimumsGreaterThanLength_Throws()
    {
        var options = new PasswordGenerationOptions(
            Length: 5,
            IncludeUppercase: true,
            IncludeLowercase: true,
            IncludeNumbers: true,
            IncludeSpecial: true,
            MinUppercase: 2,
            MinLowercase: 2,
            MinNumbers: 2,
            MinSpecial: 2);

        Assert.Throws<ArgumentException>(() => PasswordGenerator.Generate(options));
    }
}
