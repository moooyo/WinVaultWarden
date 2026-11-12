using System.Globalization;
using System.Runtime.InteropServices;

namespace App.Localization;

/// <summary>Looks up fixed application resources without examining user-provided text.</summary>
public static class UiText
{
    private sealed record ResourceState(Func<string, string?>? Resolve, CultureInfo DisplayCulture);
    private static ResourceState _state = new(null, CultureInfo.GetCultureInfo(LanguagePreferencePolicy.DefaultLanguage));

    public static CultureInfo DisplayCulture => Volatile.Read(ref _state).DisplayCulture;

    public static string Get(UiStringKey key)
    {
        var fallback = UiStringCatalog.GetFallback(key);
        var state = Volatile.Read(ref _state);
        if (state.Resolve is null)
            return fallback;

        try
        {
            var localized = state.Resolve(UiStringCatalog.GetResourceName(key));
            return string.IsNullOrEmpty(localized) ? fallback : localized;
        }
        catch (Exception exception) when (exception is COMException
            or ArgumentException or InvalidOperationException)
        {
            // A missing PRI entry must not interrupt a sensitive operation or expose its arguments.
            return fallback;
        }
    }

    public static string Format(UiStringKey key, params object?[] arguments) =>
        string.Format(DisplayCulture, Get(key), arguments);

    internal static void Configure(Func<string, string?> resolve, CultureInfo displayCulture)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(displayCulture);
        Volatile.Write(ref _state, new ResourceState(resolve, displayCulture));
    }

    internal static void UseDefaultCatalog() =>
        Volatile.Write(ref _state, new ResourceState(null,
            CultureInfo.GetCultureInfo(LanguagePreferencePolicy.DefaultLanguage)));
}
