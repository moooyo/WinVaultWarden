using CommunityToolkit.Mvvm.ComponentModel;
using App.Services;

namespace App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IAccountUiService? _accountUi;

    [ObservableProperty]
    public partial int SelectedSessionTimeoutIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedTimeoutActionIndex { get; set; }

    [ObservableProperty]
    public partial bool UsePinUnlock { get; set; }

    [ObservableProperty]
    public partial int SelectedClearClipboardIndex { get; set; }

    [ObservableProperty]
    public partial bool MinimizeOnCopy { get; set; }

    [ObservableProperty]
    public partial bool ShowIconsAndPasswordUrl { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowTrayIcon { get; set; } = true;

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; } = true;

    [ObservableProperty]
    public partial bool StartOnLogin { get; set; }

    [ObservableProperty]
    public partial bool AllowBrowserIntegration { get; set; }

    [ObservableProperty]
    public partial bool UseHardwareAcceleration { get; set; } = true;

    [ObservableProperty]
    public partial bool EnableSshAgent { get; set; } = true;

    [ObservableProperty]
    public partial int SelectedSshAuthorizationPromptIndex { get; set; }

    [ObservableProperty]
    public partial bool AllowScreenshots { get; set; } = true;

    [ObservableProperty]
    public partial int SelectedThemeIndex { get; set; } = AppPreferences.Current.ThemeIndex;

    [ObservableProperty]
    public partial int SelectedLanguageIndex { get; set; }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        AppPreferences.Current.ThemeIndex = value;
        AppPreferences.Save();
        ThemeManager.Apply(value);
    }

    public SettingsViewModel()
    {
    }

    public SettingsViewModel(IAccountUiService accountUi) => _accountUi = accountUi;

    public string AccountEmail => _accountUi?.GetAccount().Email ?? string.Empty;
    public string AccountServer => _accountUi?.GetAccount().ServerUrl ?? string.Empty;
    public string AccountInitial => _accountUi?.GetAccount().Initial ?? string.Empty;
    public string KdfSummary => _accountUi?.GetAccount().KdfSummary ?? string.Empty;

    // "关于"区:运行时真实诊断信息。
    public string AppVersion => AboutInfo.AppVersion;
    public string WindowsVersion => AboutInfo.WindowsVersion;
    public string DotNetVersion => AboutInfo.DotNetVersion;
    public string AppArchitecture => AboutInfo.Architecture;
}
