using System.Runtime.InteropServices;
using System.Globalization;
using App.Services;
using Microsoft.Windows.ApplicationModel.Resources;
using ApplicationLanguages = Microsoft.Windows.Globalization.ApplicationLanguages;

namespace App.Localization;

/// <summary>Connects typed strings and XAML x:Uid resources to Windows MRT Core.</summary>
public static class AppLocalization
{
    private static readonly LanguagePreferenceController Controller = new(
        new PreferencePersistence(), new MrtResourceRuntime());

    public static bool ResourcesAvailable { get; private set; }

    public static string CurrentPreference
    {
        get
        {
            try
            {
                return Controller.CurrentPreference;
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                return LanguagePreferencePolicy.SystemPreference;
            }
        }
    }

    public static bool Initialize()
    {
        // MRT Core keeps unpackaged overrides only in memory. Restore the separately persisted
        // preference before creating a ResourceLoader, view models, or application XAML.
        try
        {
            ResourcesAvailable = Controller.Initialize();
            if (ResourcesAvailable)
                UiText.Configure(Controller.Resolve!, CultureInfo.GetCultureInfo(Controller.EffectiveLanguage));
            else
                UiText.UseDefaultCatalog();
            return ResourcesAvailable;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            UiText.UseDefaultCatalog();
            ResourcesAvailable = false;
            return false;
        }
    }

    public static LanguagePreferenceChange SetPreference(string? preference)
    {
        try
        {
            return Controller.SetPreference(preference);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return LanguagePreferenceChange.Unavailable;
        }
    }

    private static bool IsUnavailable(Exception exception) => exception is
        COMException or InvalidOperationException or ArgumentException or NotSupportedException;

    private sealed class PreferencePersistence : ILanguagePreferencePersistence
    {
        public string ReadPreference() => AppPreferences.Current.LanguagePreference;

        public bool TryWritePreference(string preference) =>
            AppPreferences.TrySetLanguagePreference(preference);
    }

    private sealed class MrtResourceRuntime : ILanguageResourceRuntime
    {
        public IReadOnlyList<string> PreferredLanguages => Windows.System.UserProfile.GlobalizationPreferences.Languages;

        public bool TryApplyPreference(string preference)
        {
            if (!string.Equals(ApplicationLanguages.PrimaryLanguageOverride, preference, StringComparison.Ordinal))
                ApplicationLanguages.PrimaryLanguageOverride = preference;
            return true;
        }

        public bool TryCreateResolver(out Func<string, string?>? resolve)
        {
            var loader = new ResourceLoader();
            if (string.IsNullOrEmpty(loader.GetString(nameof(UiStringKey.Text2CD0F3BE8738A86C))))
            {
                resolve = null;
                return false;
            }
            resolve = loader.GetString;
            return true;
        }
    }
}
