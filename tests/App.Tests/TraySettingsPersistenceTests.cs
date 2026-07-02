using App.Services;
using App.ViewModels;
using Xunit;

namespace App.Tests;

public class TraySettingsPersistenceTests
{
    [Fact]
    public void ShowTrayIcon_Persists_To_AppPreferences()
    {
        var vm = new SettingsViewModel();
        vm.ShowTrayIcon = false;
        Assert.False(AppPreferences.Current.ShowTrayIcon);
    }

    [Fact]
    public void ShowTrayIcon_Initializes_From_AppPreferences()
    {
        AppPreferences.Current.ShowTrayIcon = false;
        var vm = new SettingsViewModel();
        Assert.False(vm.ShowTrayIcon);
    }

    [Fact]
    public void MinimizeToTray_Persists_To_AppPreferences()
    {
        var vm = new SettingsViewModel();
        vm.MinimizeToTray = true;
        Assert.True(AppPreferences.Current.MinimizeToTray);
    }

    [Fact]
    public void CloseToTray_Persists_To_AppPreferences()
    {
        var vm = new SettingsViewModel();
        vm.CloseToTray = false;
        Assert.False(AppPreferences.Current.CloseToTray);
    }

    [Fact]
    public void ShowTrayIcon_Change_Raises_Static_Event()
    {
        // Reset to default true state
        AppPreferences.Current.ShowTrayIcon = true;
        bool? received = null;
        void Handler(bool v) => received = v;
        SettingsViewModel.ShowTrayIconSettingChanged += Handler;
        try
        {
            var vm = new SettingsViewModel();
            vm.ShowTrayIcon = false;
            Assert.Equal(false, received);
        }
        finally
        {
            SettingsViewModel.ShowTrayIconSettingChanged -= Handler;
        }
    }
}
