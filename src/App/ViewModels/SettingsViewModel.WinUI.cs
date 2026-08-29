using App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace App.ViewModels;

/// <summary>
/// SettingsViewModel 的 WinUI 专属部分：主题切换、AboutInfo 等依赖 Windows/WinUI 运行时的成员。
/// 此文件仅在真实 App 项目（WinUI 打包）中编译，不在 App.Tests 中链接。
/// </summary>
public partial class SettingsViewModel
{
    private int _selectedThemeIndex = LoadThemeIndex();

    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set
        {
            if (AppPreferenceIndexPolicy.IsTransientNoSelection(value))
            {
                OnPropertyChanged(nameof(SelectedThemeIndex));
                return;
            }
            var normalized = AppPreferenceIndexPolicy.NormalizeThemeIndex(value);
            var changed = SetProperty(ref _selectedThemeIndex, normalized);
            SaveThemeIndex(normalized);
            if (changed)
                ThemeManager.Apply(normalized);
        }
    }

    private static int LoadThemeIndex()
    {
        var normalized = AppPreferenceIndexPolicy.NormalizeThemeIndex(
            AppPreferences.Current.ThemeIndex);
        SaveThemeIndex(normalized);
        return normalized;
    }

    private static void SaveThemeIndex(int value)
    {
        if (AppPreferences.Current.ThemeIndex == value)
            return;
        AppPreferences.Current.ThemeIndex = value;
        AppPreferences.Save();
    }

    [ObservableProperty]
    public partial bool ShowWebsiteIcons { get; set; } = AppPreferences.Current.ShowWebsiteIcons;

    partial void OnShowWebsiteIconsChanged(bool value)
    {
        AppPreferences.Current.ShowWebsiteIcons = value;
        AppPreferences.Save();
    }

    // "关于"区:运行时真实诊断信息。
    public string AppVersion => AboutInfo.AppVersion;
    public string WindowsVersion => AboutInfo.WindowsVersion;
    public string DotNetVersion => AboutInfo.DotNetVersion;
    public string AppArchitecture => AboutInfo.Architecture;
}
