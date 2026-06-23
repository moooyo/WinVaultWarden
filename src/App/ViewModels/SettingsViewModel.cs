using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    // 0=跟随系统 1=浅色 2=深色
    [ObservableProperty] private int _selectedThemeIndex;

    public string AccountEmail => "test@test.com";
    public string AccountInitial => "T";
    public string KdfSummary => "PBKDF2 · 600000 次迭代";

    partial void OnSelectedThemeIndexChanged(int value) => ApplyTheme(value);

    private static void ApplyTheme(int index)
    {
        if (global::App.App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = index switch
            {
                1 => ElementTheme.Light,
                2 => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }
}
