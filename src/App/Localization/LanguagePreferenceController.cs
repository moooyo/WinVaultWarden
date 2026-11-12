namespace App.Localization;

public enum LanguagePreferenceChange
{
    Unchanged,
    RestartRequired,
    InvalidPreference,
    Unavailable,
}

public interface ILanguagePreferencePersistence
{
    string? ReadPreference();
    bool TryWritePreference(string preference);
}

public interface ILanguageResourceRuntime
{
    IReadOnlyList<string> PreferredLanguages { get; }
    bool TryApplyPreference(string preference);
    bool TryCreateResolver(out Func<string, string?>? resolve);
}

/// <summary>Persists portable language choices and applies them only at the next startup.</summary>
public sealed class LanguagePreferenceController(
    ILanguagePreferencePersistence persistence,
    ILanguageResourceRuntime runtime)
{
    public Func<string, string?>? Resolve { get; private set; }
    public string EffectiveLanguage { get; private set; } = LanguagePreferencePolicy.DefaultLanguage;
    public string CurrentPreference => LanguagePreferencePolicy.Normalize(persistence.ReadPreference());

    public bool Initialize()
    {
        Resolve = null;
        var preference = CurrentPreference;
        EffectiveLanguage = LanguagePreferencePolicy.ResolveLanguage(preference, runtime.PreferredLanguages);
        // Resolve system mode from the user's Windows language list on every startup. Applying
        // a real supported tag avoids stale packaged-app overrides and invalid empty BCP-47 tags.
        return runtime.TryApplyPreference(EffectiveLanguage)
            && runtime.TryCreateResolver(out var resolve)
            && (Resolve = resolve) is not null;
    }

    public LanguagePreferenceChange SetPreference(string? preference)
    {
        if (!LanguagePreferencePolicy.TryNormalize(preference, out var normalized))
            return LanguagePreferenceChange.InvalidPreference;
        if (string.Equals(CurrentPreference, normalized, StringComparison.Ordinal))
            return LanguagePreferenceChange.Unchanged;

        // The current resource loader and every x:Uid stay in the current language until restart.
        // Persisting first also prevents a failed write from changing only the process language.
        return persistence.TryWritePreference(normalized)
            ? LanguagePreferenceChange.RestartRequired
            : LanguagePreferenceChange.Unavailable;
    }
}
