namespace App.Localization;

/// <summary>Maps persisted language preferences to the app's complete resource sets.</summary>
public static class LanguagePreferencePolicy
{
    public const string SystemPreference = "";
    public const string DefaultLanguage = "zh-CN";

    private static readonly string[] Tags = ["", "zh-CN", "zh-TW", "en-US", "ja-JP"];

    public static IReadOnlyList<string> SupportedPreferences { get; } = Array.AsReadOnly(Tags);

    public static bool TryNormalize(string? preference, out string normalized)
    {
        normalized = SystemPreference;
        if (preference is null)
            return true;

        var trimmed = preference.Trim();
        foreach (var tag in Tags)
        {
            if (string.Equals(tag, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                normalized = tag;
                return true;
            }
        }

        return false;
    }

    public static string Normalize(string? preference) =>
        TryNormalize(preference, out var normalized) ? normalized : SystemPreference;

    public static bool TryGetPreference(int selectedIndex, out string preference)
    {
        if (selectedIndex < 0 || selectedIndex >= Tags.Length)
        {
            preference = SystemPreference;
            return false;
        }

        preference = Tags[selectedIndex];
        return true;
    }

    public static int GetSelectedIndex(string? preference)
    {
        var normalized = Normalize(preference);
        return Array.IndexOf(Tags, normalized);
    }

    public static string ResolveLanguage(string? preference, IEnumerable<string>? preferredLanguages)
    {
        var normalized = Normalize(preference);
        if (normalized.Length != 0)
            return normalized;

        if (preferredLanguages is not null)
        {
            foreach (var preferred in preferredLanguages)
            {
                var match = MatchSupportedLanguage(preferred);
                if (match is not null)
                    return match;
            }
        }

        return DefaultLanguage;
    }

    private static string? MatchSupportedLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Length > 85)
            return null;

        var parts = language.Trim().Split('-');
        if (parts.Any(part => part.Length == 0
            || part.Any(character => !char.IsAsciiLetterOrDigit(character))))
            return null;

        if (parts[0].Equals("en", StringComparison.OrdinalIgnoreCase))
            return "en-US";
        if (parts[0].Equals("ja", StringComparison.OrdinalIgnoreCase))
            return "ja-JP";
        if (!parts[0].Equals("zh", StringComparison.OrdinalIgnoreCase))
            return null;

        // An explicit script takes precedence over a region (for example zh-Hans-HK).
        if (parts.Any(part => part.Equals("Hans", StringComparison.OrdinalIgnoreCase)))
            return "zh-CN";
        if (parts.Any(part => part.Equals("Hant", StringComparison.OrdinalIgnoreCase)))
            return "zh-TW";
        return parts.Any(part => part.Equals("TW", StringComparison.OrdinalIgnoreCase)
            || part.Equals("HK", StringComparison.OrdinalIgnoreCase)
            || part.Equals("MO", StringComparison.OrdinalIgnoreCase))
            ? "zh-TW"
            : "zh-CN";
    }
}
