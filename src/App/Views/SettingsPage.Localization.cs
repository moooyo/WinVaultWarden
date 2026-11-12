using App.Localization;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class SettingsPage
{
    private bool _initializingLanguageSelection;

    private void InitializeLanguageSettings()
    {
        _initializingLanguageSelection = true;
        try
        {
            LanguageSelector.IsEnabled = AppLocalization.ResourcesAvailable;
            LanguageSelector.SelectedIndex = LanguagePreferencePolicy.GetSelectedIndex(
                AppLocalization.CurrentPreference);
        }
        finally
        {
            _initializingLanguageSelection = false;
        }
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_initializingLanguageSelection || !IsLoaded
            || !LanguagePreferencePolicy.TryGetPreference(LanguageSelector.SelectedIndex, out var preference))
            return;

        var result = AppLocalization.SetPreference(preference);
        switch (result)
        {
            case LanguagePreferenceChange.RestartRequired:
                ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text493A610D27BBC5B3), InfoBarSeverity.Informational);
                break;
            case LanguagePreferenceChange.InvalidPreference:
            case LanguagePreferenceChange.Unavailable:
                InitializeLanguageSettings();
                ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text771BB4772E7AD4AA), InfoBarSeverity.Error);
                break;
        }
    }
}
