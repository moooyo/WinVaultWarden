using App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace App.ViewModels;

/// <summary>
/// SettingsViewModel 的 WinUI 专属部分：主题切换、AboutInfo 等依赖 Windows/WinUI 运行时的成员。
/// 此文件仅在真实 App 项目（WinUI 打包）中编译，不在 App.Tests 中链接。
/// </summary>
public partial class SettingsViewModel
{
    [ObservableProperty]
    public partial int SelectedThemeIndex { get; set; } = AppPreferences.Current.ThemeIndex;

    partial void OnSelectedThemeIndexChanged(int value)
    {
        AppPreferences.Current.ThemeIndex = value;
        AppPreferences.Save();
        ThemeManager.Apply(value);
    }

    // "关于"区:运行时真实诊断信息。
    public string AppVersion => AboutInfo.AppVersion;
    public string WindowsVersion => AboutInfo.WindowsVersion;
    public string DotNetVersion => AboutInfo.DotNetVersion;
    public string AppArchitecture => AboutInfo.Architecture;
}
