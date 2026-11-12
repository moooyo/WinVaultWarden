using App.Localization;
using Xunit;

namespace App.Tests;

public sealed class LanguagePreferencePolicyTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  zh-cn  ", "zh-CN")]
    [InlineData("ZH-TW", "zh-TW")]
    [InlineData("en-us", "en-US")]
    [InlineData("ja-JP", "ja-JP")]
    public void NormalizesOnlySupportedPreferences(string? input, string expected)
    {
        Assert.True(LanguagePreferencePolicy.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en-US;zh-CN")]
    [InlineData("../../en-US")]
    [InlineData("en_US")]
    [InlineData("zh-HK")]
    public void RejectsUnsupportedExplicitOverrides(string input) =>
        Assert.False(LanguagePreferencePolicy.TryNormalize(input, out _));

    [Theory]
    [InlineData("en-GB", "en-US")]
    [InlineData("ja", "ja-JP")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-Hans-HK", "zh-CN")]
    [InlineData("zh-SG", "zh-CN")]
    public void SystemPreferenceMatchesLanguageAndScript(string input, string expected) =>
        Assert.Equal(expected, LanguagePreferencePolicy.ResolveLanguage("", [input]));

    [Fact]
    public void ExplicitPreferenceTakesPrecedenceOverWindowsLanguages() =>
        Assert.Equal("ja-JP", LanguagePreferencePolicy.ResolveLanguage("ja-JP", ["zh-CN", "en-US"]));

    [Fact]
    public void UnsupportedSystemLanguageUsesDeclaredDefault() =>
        Assert.Equal(LanguagePreferencePolicy.DefaultLanguage,
            LanguagePreferencePolicy.ResolveLanguage("", ["fr-FR", "invalid--tag"]));

    [Fact]
    public void TransientAndInvalidSelectionIndicesDoNotProducePreferences()
    {
        Assert.False(LanguagePreferencePolicy.TryGetPreference(-1, out _));
        Assert.False(LanguagePreferencePolicy.TryGetPreference(5, out _));
        Assert.False(LanguagePreferencePolicy.TryGetPreference(int.MaxValue, out _));
    }

    [Fact]
    public void EverySelectionRoundTripsThroughCanonicalPreference()
    {
        for (var index = 0; index < LanguagePreferencePolicy.SupportedPreferences.Count; index++)
        {
            Assert.True(LanguagePreferencePolicy.TryGetPreference(index, out var preference));
            Assert.Equal(index, LanguagePreferencePolicy.GetSelectedIndex(preference));
        }
    }
}
