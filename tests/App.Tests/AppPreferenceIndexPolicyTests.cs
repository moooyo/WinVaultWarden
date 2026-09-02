using App.Services;
using System.Text.Json;
using Xunit;

namespace App.Tests;

public sealed class AppPreferenceIndexPolicyTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(int.MaxValue)]
    public void InvalidSessionTimeoutIndex_UsesSafeDefault(int value)
    {
        Assert.Equal(
            AppPreferenceIndexPolicy.DefaultSessionTimeoutIndex,
            AppPreferenceIndexPolicy.NormalizeSessionTimeoutIndex(value));
        Assert.Equal(15, IdleTimeoutPolicy.MinutesForIndex(value));
    }

    [Fact]
    public void Normalize_ReplacesEveryInvalidIndex()
    {
        var preferences = new AppPreferencesData
        {
            ThemeIndex = 99,
            SessionTimeoutIndex = -1,
            TimeoutActionIndex = 2,
            ClearClipboardIndex = 7,
        };

        Assert.True(AppPreferenceIndexPolicy.Normalize(preferences));

        Assert.Equal(AppPreferenceIndexPolicy.DefaultThemeIndex, preferences.ThemeIndex);
        Assert.Equal(AppPreferenceIndexPolicy.DefaultSessionTimeoutIndex, preferences.SessionTimeoutIndex);
        Assert.Equal(AppPreferenceIndexPolicy.DefaultTimeoutActionIndex, preferences.TimeoutActionIndex);
        Assert.Equal(AppPreferenceIndexPolicy.DefaultClearClipboardIndex, preferences.ClearClipboardIndex);
        Assert.False(AppPreferenceIndexPolicy.Normalize(preferences));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(8)]
    public void ExplicitNonIdleTimeoutOptions_RemainDisabled(int value)
    {
        Assert.Equal(value, AppPreferenceIndexPolicy.NormalizeSessionTimeoutIndex(value));
        Assert.Equal(0, IdleTimeoutPolicy.MinutesForIndex(value));
    }

    [Fact]
    public void Load_RepairsInvalidIndicesOnDisk()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinVaultWarden.Tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "preferences.json");
        Directory.CreateDirectory(directory);

        try
        {
            var invalid = new AppPreferencesData
            {
                ThemeIndex = 99,
                SessionTimeoutIndex = -1,
                TimeoutActionIndex = 2,
                ClearClipboardIndex = 7,
            };
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(invalid, AppJsonContext.Default.AppPreferencesData));

            var loaded = AppPreferences.LoadFromPathForTesting(path);
            var persisted = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                AppJsonContext.Default.AppPreferencesData)!;

            Assert.Equal(AppPreferenceIndexPolicy.DefaultThemeIndex, loaded.ThemeIndex);
            Assert.Equal(AppPreferenceIndexPolicy.DefaultSessionTimeoutIndex, loaded.SessionTimeoutIndex);
            Assert.Equal(AppPreferenceIndexPolicy.DefaultTimeoutActionIndex, loaded.TimeoutActionIndex);
            Assert.Equal(AppPreferenceIndexPolicy.DefaultClearClipboardIndex, loaded.ClearClipboardIndex);
            Assert.Equal(loaded.ThemeIndex, persisted.ThemeIndex);
            Assert.Equal(loaded.SessionTimeoutIndex, persisted.SessionTimeoutIndex);
            Assert.Equal(loaded.TimeoutActionIndex, persisted.TimeoutActionIndex);
            Assert.Equal(loaded.ClearClipboardIndex, persisted.ClearClipboardIndex);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
