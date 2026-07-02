using App.Services;
using App.ViewModels;
using Xunit;

namespace App.Tests;

public class SettingsTimeoutPersistenceTests
{
    [Fact]
    public void SessionTimeoutIndex_Persists_To_AppPreferences()
    {
        var vm = new SettingsViewModel();
        vm.SelectedSessionTimeoutIndex = 4;               // 30 min
        Assert.Equal(4, AppPreferences.Current.SessionTimeoutIndex);
    }

    [Fact]
    public void TimeoutActionIndex_Persists_To_AppPreferences()
    {
        var vm = new SettingsViewModel();
        vm.SelectedTimeoutActionIndex = 1;                // Logout
        Assert.Equal(1, AppPreferences.Current.TimeoutActionIndex);
    }

    [Fact]
    public void SessionTimeoutIndex_Initializes_From_AppPreferences()
    {
        AppPreferences.Current.SessionTimeoutIndex = 5;   // 1 hour
        var vm = new SettingsViewModel();
        Assert.Equal(5, vm.SelectedSessionTimeoutIndex);
    }
}
