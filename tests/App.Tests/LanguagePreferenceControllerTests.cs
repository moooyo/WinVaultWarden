using App.Localization;
using Xunit;

namespace App.Tests;

public sealed class LanguagePreferenceControllerTests
{
    [Fact]
    public void RecreatedPortableRuntimeRestoresThePersistedChoiceBeforeLoadingResources()
    {
        var persistence = new MemoryPersistence("zh-CN");
        var firstRuntime = new MemoryRuntime();
        var first = new LanguagePreferenceController(persistence, firstRuntime);
        Assert.True(first.Initialize());
        Assert.Equal(LanguagePreferenceChange.RestartRequired, first.SetPreference("ja-JP"));
        Assert.Equal("zh-CN", firstRuntime.Override);

        var nextRuntime = new MemoryRuntime();
        var next = new LanguagePreferenceController(persistence, nextRuntime);
        Assert.True(next.Initialize());
        Assert.Equal("ja-JP", nextRuntime.Override);
        Assert.Equal("ja-JP", nextRuntime.PreferenceAtResourceCreation);
        Assert.Equal("ja-JP", next.EffectiveLanguage);
    }

    [Fact]
    public void FailedPersistenceKeepsBothTheSavedAndActiveLanguages()
    {
        var persistence = new MemoryPersistence("zh-CN") { CanWrite = false };
        var runtime = new MemoryRuntime();
        var controller = new LanguagePreferenceController(persistence, runtime);
        Assert.True(controller.Initialize());

        Assert.Equal(LanguagePreferenceChange.Unavailable, controller.SetPreference("en-US"));
        Assert.Equal("zh-CN", persistence.ReadPreference());
        Assert.Equal("zh-CN", runtime.Override);
        Assert.Equal(1, runtime.ApplyCalls);
    }

    [Fact]
    public void SystemChoiceAppliesARealSupportedUserLanguageWithoutAnEmptyOverride()
    {
        var runtime = new MemoryRuntime { Languages = ["fr-FR", "zh-Hant-HK", "en-US"] };
        var controller = new LanguagePreferenceController(new MemoryPersistence(""), runtime);
        Assert.True(controller.Initialize());
        Assert.Equal("zh-TW", runtime.Override);
        Assert.Equal("", controller.CurrentPreference);
        Assert.Equal("zh-TW", controller.EffectiveLanguage);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UnavailableRuntimeNeverPublishesAResourceResolver(bool canApply, bool canLoad)
    {
        var runtime = new MemoryRuntime { CanApply = canApply, CanLoad = canLoad };
        var controller = new LanguagePreferenceController(new MemoryPersistence("en-US"), runtime);
        Assert.False(controller.Initialize());
        Assert.Null(controller.Resolve);
    }

    [Fact]
    public void InvalidPreferenceDoesNotWriteOrMutateTheRuntime()
    {
        var persistence = new MemoryPersistence("zh-CN");
        var runtime = new MemoryRuntime();
        var controller = new LanguagePreferenceController(persistence, runtime);
        Assert.Equal(LanguagePreferenceChange.InvalidPreference, controller.SetPreference("en-US;ja-JP"));
        Assert.Equal(0, persistence.Writes);
        Assert.Equal(0, runtime.ApplyCalls);
    }

    private sealed class MemoryPersistence(string preference) : ILanguagePreferencePersistence
    {
        public bool CanWrite { get; set; } = true;
        public int Writes { get; private set; }
        public string ReadPreference() => preference;
        public bool TryWritePreference(string value)
        {
            Writes++;
            if (!CanWrite)
                return false;
            preference = value;
            return true;
        }
    }

    private sealed class MemoryRuntime : ILanguageResourceRuntime
    {
        public IReadOnlyList<string> Languages { get; set; } = ["en-GB"];
        public IReadOnlyList<string> PreferredLanguages => Languages;
        public bool CanApply { get; set; } = true;
        public bool CanLoad { get; set; } = true;
        public string Override { get; private set; } = "";
        public string? PreferenceAtResourceCreation { get; private set; }
        public int ApplyCalls { get; private set; }

        public bool TryApplyPreference(string preference)
        {
            ApplyCalls++;
            if (!CanApply)
                return false;
            Override = preference;
            return true;
        }

        public bool TryCreateResolver(out Func<string, string?>? resolve)
        {
            PreferenceAtResourceCreation = Override;
            resolve = CanLoad ? key => "resource:" + key : null;
            return CanLoad;
        }
    }
}
