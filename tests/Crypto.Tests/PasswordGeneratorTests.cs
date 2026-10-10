using Crypto;
using Xunit;

namespace Crypto.Tests;

[Trait("Category", "Crypto")]
public class PasswordGeneratorTests
{
    private const string AmbiguousCharacters = "IOl01";
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
    public void Generate_NoCharacterSetSelectedWithAvoidAmbiguous_FallsBackToUnmistakableLowercase()
    {
        var passwords = Enumerable.Range(0, 8)
            .Select(_ => PasswordGenerator.Generate(new PasswordGenerationOptions(
                Length: 128,
                IncludeUppercase: false,
                IncludeLowercase: false,
                IncludeNumbers: false,
                IncludeSpecial: false,
                MinUppercase: 0,
                MinLowercase: 0,
                MinNumbers: 0,
                MinSpecial: 0,
                AvoidAmbiguous: true)))
            .ToList();

        Assert.All(passwords, password =>
        {
            Assert.Equal(128, password.Length);
            Assert.DoesNotContain(password, c => AmbiguousCharacters.Contains(c));
        });
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
    public void GeneratePassphrase_DefaultWordCount_ReturnsSixWords()
    {
        var defaults = new PassphraseGenerationOptions();
        var passphrase = PasswordGenerator.GeneratePassphrase(defaults with { Separator = "|" });

        Assert.Equal(6, defaults.WordCount);
        Assert.Equal(defaults.WordCount, passphrase.Split('|').Length);
    }

    [Fact]
    public void GeneratePassphrase_DrawsFromLargeWordlist_HighDiversity()
    {
        // H1/L5: 词表须是大表(EFF 7776),不能是旧 22 词占位表。
        // 收集大量单词,distinct 数远超 22 即证明词表已扩大(6 词 ≈ 77.5 bits 而非 ~27)。
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 200; i++)
            foreach (var w in PasswordGenerator.GeneratePassphrase(
                         new PassphraseGenerationOptions(WordCount: 10, Separator: "|", Capitalize: false, IncludeNumber: false))
                     .Split('|'))
                distinct.Add(w);

        // 2000 次抽样:22 词表最多 22 个 distinct;大表应有数百个。
        Assert.True(distinct.Count > 500, $"词表过小,distinct={distinct.Count}(疑似仍是占位表)");
    }

    [Fact]
    public void GeneratePassphrase_CapitalizeAndNumber_AppliesOptions()
    {
        var passphrase = PasswordGenerator.GeneratePassphrase(
            new PassphraseGenerationOptions(WordCount: 4, Separator: ".", Capitalize: true, IncludeNumber: true));

        var words = passphrase.Split('.');
        Assert.Equal(4, words.Length);
        Assert.All(words, word => Assert.True(char.IsUpper(word[0])));
        Assert.Single(words.Where(word => word.Any(char.IsDigit)));
        Assert.Single(passphrase.Where(char.IsDigit));
        Assert.True(char.IsDigit(words.Single(word => word.Any(char.IsDigit))[^1]));
    }

    [Fact]
    public void GenerateUsername_RandomWord_ReturnsSingleWord()
    {
        var username = PasswordGenerator.GenerateUsername();

        Assert.False(string.IsNullOrWhiteSpace(username));
        Assert.DoesNotContain(' ', username);
        Assert.DoesNotContain("@", username);
    }

    [Fact]
    public void GenerateUsername_RandomWordWithNumber_AppendsFourDigits()
    {
        var username = PasswordGenerator.GenerateUsername(
            new UsernameGenerationOptions(
                Type: UsernameGenerationType.RandomWord,
                IncludeNumber: true));

        Assert.True(username.Length > 4);
        Assert.All(username[^4..], character => Assert.True(char.IsAsciiDigit(character)));
        Assert.DoesNotContain(username[..^4], char.IsDigit);
    }

    [Fact]
    public void GenerateUsername_LegacyPositionalConstruction_RemainsCompatible()
    {
        var options = new UsernameGenerationOptions(
            UsernameGenerationType.RandomWord,
            true,
            true);

        var username = PasswordGenerator.GenerateUsername(options);

        Assert.True(char.IsUpper(username[0]));
        Assert.All(username[^4..], character => Assert.True(char.IsAsciiDigit(character)));
        Assert.Null(options.Email);
        Assert.Null(options.Domain);
    }

    [Fact]
    public void GenerateUsername_PlusAddress_AppendsEightRandomCharacters()
    {
        var username = PasswordGenerator.GenerateUsername(new UsernameGenerationOptions(
            Type: UsernameGenerationType.PlusAddressedEmail,
            Email: "user@example.com"));

        const string prefix = "user+";
        const string suffix = "@example.com";
        Assert.StartsWith(prefix, username, StringComparison.Ordinal);
        Assert.EndsWith(suffix, username, StringComparison.Ordinal);
        AssertUsernameRandomPart(username[prefix.Length..^suffix.Length]);
    }

    [Fact]
    public void GenerateUsername_PlusAddress_ExtendsExistingSubaddress()
    {
        var username = PasswordGenerator.GenerateUsername(new UsernameGenerationOptions(
            Type: UsernameGenerationType.PlusAddressedEmail,
            Email: "user+existing@example.com"));

        const string prefix = "user+existing";
        const string suffix = "@example.com";
        Assert.StartsWith(prefix, username, StringComparison.Ordinal);
        Assert.EndsWith(suffix, username, StringComparison.Ordinal);
        AssertUsernameRandomPart(username[prefix.Length..^suffix.Length]);
    }

    [Fact]
    public void GenerateUsername_CatchAll_UsesEightCharacterLocalPart()
    {
        var username = PasswordGenerator.GenerateUsername(new UsernameGenerationOptions(
            Type: UsernameGenerationType.CatchAllEmail,
            Domain: "example.com"));

        var parts = username.Split('@');
        Assert.Equal(2, parts.Length);
        Assert.Equal("example.com", parts[1]);
        AssertUsernameRandomPart(parts[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("user@@example.com")]
    [InlineData("user @example.com")]
    [InlineData("user@exam\tple.com")]
    [InlineData("user@example.com\n")]
    [InlineData("+tag@example.com")]
    [InlineData("user+@example.com")]
    [InlineData("user@example.com/path")]
    [InlineData("user@.")]
    public void GenerateUsername_PlusAddress_RejectsInvalidEmail(string? email)
    {
        var options = new UsernameGenerationOptions(
            Type: UsernameGenerationType.PlusAddressedEmail,
            Email: email);

        Assert.Throws<ArgumentException>(() => PasswordGenerator.GenerateUsername(options));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("@")]
    [InlineData("@example.com")]
    [InlineData("example.com@")]
    [InlineData("example@com")]
    [InlineData("exam ple.com")]
    [InlineData("example.com\r")]
    [InlineData("example.com/path")]
    [InlineData(".")]
    public void GenerateUsername_CatchAll_RejectsInvalidDomain(string? domain)
    {
        var options = new UsernameGenerationOptions(
            Type: UsernameGenerationType.CatchAllEmail,
            Domain: domain);

        Assert.Throws<ArgumentException>(() => PasswordGenerator.GenerateUsername(options));
    }

    [Fact]
    public void GenerateUsername_CatchAll_RejectsDomainLongerThanDnsLimit()
    {
        var options = new UsernameGenerationOptions(
            Type: UsernameGenerationType.CatchAllEmail,
            Domain: new string('a', 254));

        Assert.Throws<ArgumentException>(() => PasswordGenerator.GenerateUsername(options));
    }

    [Fact]
    public void GenerateUsername_ForwardedEmailAlias_RemainsUnsupported()
    {
        var options = new UsernameGenerationOptions(
            Type: UsernameGenerationType.ForwardedEmailAlias,
            Email: "user@example.com",
            Domain: "example.com");

        Assert.Throws<NotSupportedException>(() => PasswordGenerator.GenerateUsername(options));
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

    [Fact]
    public void Generate_DisabledCharacterSetMinimumsGreaterThanLength_Throws()
    {
        var options = new PasswordGenerationOptions(
            Length: 5,
            IncludeUppercase: true,
            IncludeLowercase: true,
            IncludeNumbers: false,
            IncludeSpecial: false,
            MinUppercase: 0,
            MinLowercase: 0,
            MinNumbers: 6,
            MinSpecial: 0);

        Assert.Throws<ArgumentException>(() => PasswordGenerator.Generate(options));
    }

    private static void AssertUsernameRandomPart(string value)
    {
        const string allowed = "abcdefghijklmnopqrstuvwxyz1234567890";
        Assert.Equal(8, value.Length);
        Assert.All(value, character => Assert.Contains(character, allowed));
    }
}
