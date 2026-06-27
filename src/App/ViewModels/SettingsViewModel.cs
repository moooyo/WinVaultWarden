using CommunityToolkit.Mvvm.ComponentModel;
using App.Services;

namespace App.ViewModels;

public class SettingsViewModel : ObservableObject
{
    private readonly IAccountUiService? _accountUi;
    private int _selectedSessionTimeoutIndex;
    private int _selectedTimeoutActionIndex;
    private bool _usePinUnlock;
    private int _selectedClearClipboardIndex;
    private bool _minimizeOnCopy;
    private bool _showIconsAndPasswordUrl = true;
    private bool _showTrayIcon = true;
    private bool _minimizeToTray;
    private bool _closeToTray = true;
    private bool _startOnLogin;
    private bool _allowBrowserIntegration;
    private bool _useHardwareAcceleration = true;
    private bool _enableSshAgent = true;
    private int _selectedSshAuthorizationPromptIndex;
    private bool _allowScreenshots = true;
    private int _selectedThemeIndex = AppPreferences.Current.ThemeIndex;
    private int _selectedLanguageIndex;

    public SettingsViewModel()
    {
    }

    public SettingsViewModel(IAccountUiService accountUi) => _accountUi = accountUi;

    public int SelectedSessionTimeoutIndex
    {
        get => _selectedSessionTimeoutIndex;
        set => SetProperty(ref _selectedSessionTimeoutIndex, value);
    }

    public int SelectedTimeoutActionIndex
    {
        get => _selectedTimeoutActionIndex;
        set => SetProperty(ref _selectedTimeoutActionIndex, value);
    }

    public bool UsePinUnlock
    {
        get => _usePinUnlock;
        set => SetProperty(ref _usePinUnlock, value);
    }

    public int SelectedClearClipboardIndex
    {
        get => _selectedClearClipboardIndex;
        set => SetProperty(ref _selectedClearClipboardIndex, value);
    }

    public bool MinimizeOnCopy
    {
        get => _minimizeOnCopy;
        set => SetProperty(ref _minimizeOnCopy, value);
    }

    public bool ShowIconsAndPasswordUrl
    {
        get => _showIconsAndPasswordUrl;
        set => SetProperty(ref _showIconsAndPasswordUrl, value);
    }

    public bool ShowTrayIcon
    {
        get => _showTrayIcon;
        set => SetProperty(ref _showTrayIcon, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetProperty(ref _minimizeToTray, value);
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set => SetProperty(ref _closeToTray, value);
    }

    public bool StartOnLogin
    {
        get => _startOnLogin;
        set => SetProperty(ref _startOnLogin, value);
    }

    public bool AllowBrowserIntegration
    {
        get => _allowBrowserIntegration;
        set => SetProperty(ref _allowBrowserIntegration, value);
    }

    public bool UseHardwareAcceleration
    {
        get => _useHardwareAcceleration;
        set => SetProperty(ref _useHardwareAcceleration, value);
    }

    public bool EnableSshAgent
    {
        get => _enableSshAgent;
        set => SetProperty(ref _enableSshAgent, value);
    }

    public int SelectedSshAuthorizationPromptIndex
    {
        get => _selectedSshAuthorizationPromptIndex;
        set => SetProperty(ref _selectedSshAuthorizationPromptIndex, value);
    }

    public bool AllowScreenshots
    {
        get => _allowScreenshots;
        set => SetProperty(ref _allowScreenshots, value);
    }

    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set
        {
            if (SetProperty(ref _selectedThemeIndex, value))
            {
                AppPreferences.Current.ThemeIndex = value;
                AppPreferences.Save();
                ThemeManager.Apply(value);
            }
        }
    }

    public int SelectedLanguageIndex
    {
        get => _selectedLanguageIndex;
        set => SetProperty(ref _selectedLanguageIndex, value);
    }

    public string AccountEmail => _accountUi?.GetAccount().Email ?? string.Empty;
    public string AccountServer => _accountUi?.GetAccount().ServerUrl ?? string.Empty;
    public string AccountInitial => _accountUi?.GetAccount().Initial ?? string.Empty;
    public string KdfSummary => _accountUi?.GetAccount().KdfSummary ?? string.Empty;

    // “关于”区:运行时真实诊断信息。
    public string AppVersion => AboutInfo.AppVersion;
    public string WindowsVersion => AboutInfo.WindowsVersion;
    public string DotNetVersion => AboutInfo.DotNetVersion;
    public string AppArchitecture => AboutInfo.Architecture;
}
