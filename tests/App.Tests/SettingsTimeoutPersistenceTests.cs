using App.Services;
using App.ViewModels;
using Xunit;

namespace App.Tests;

[Collection(TestCollectionNames.AppPreferences)]
public class SettingsTimeoutPersistenceTests : AppPreferencesTestBase
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

    [Fact]
    public void SessionTimeoutIndex_InvalidSetter_NormalizesToSafeDefault()
    {
        var vm = new SettingsViewModel();

        vm.SelectedSessionTimeoutIndex = 99;

        Assert.Equal(AppPreferenceIndexPolicy.DefaultSessionTimeoutIndex, vm.SelectedSessionTimeoutIndex);
        Assert.Equal(AppPreferenceIndexPolicy.DefaultSessionTimeoutIndex, AppPreferences.Current.SessionTimeoutIndex);
    }

    [Fact]
    public void SessionTimeoutIndex_InvalidStoredValue_NormalizesOnViewModelLoad()
    {
        AppPreferences.Current.SessionTimeoutIndex = -1;

        var vm = new SettingsViewModel();

        Assert.Equal(AppPreferenceIndexPolicy.DefaultSessionTimeoutIndex, vm.SelectedSessionTimeoutIndex);
        Assert.Equal(AppPreferenceIndexPolicy.DefaultSessionTimeoutIndex, AppPreferences.Current.SessionTimeoutIndex);
    }

    [Fact]
    public void TimeoutActionIndex_InvalidSetter_NormalizesToLock()
    {
        var vm = new SettingsViewModel();

        vm.SelectedTimeoutActionIndex = 99;

        Assert.Equal(AppPreferenceIndexPolicy.DefaultTimeoutActionIndex, vm.SelectedTimeoutActionIndex);
        Assert.Equal(AppPreferenceIndexPolicy.DefaultTimeoutActionIndex, AppPreferences.Current.TimeoutActionIndex);
    }

    [Fact]
    public void SessionTimeoutIndex_TransientNoSelection_PreservesCurrentValue()
    {
        AppPreferences.Current.SessionTimeoutIndex = 5;
        var vm = new SettingsViewModel();

        vm.SelectedSessionTimeoutIndex = AppPreferenceIndexPolicy.NoSelectionIndex;

        Assert.Equal(5, vm.SelectedSessionTimeoutIndex);
        Assert.Equal(5, AppPreferences.Current.SessionTimeoutIndex);
    }

    [Fact]
    public void TimeoutActionIndex_TransientNoSelection_PreservesCurrentValue()
    {
        AppPreferences.Current.TimeoutActionIndex = 1;
        var vm = new SettingsViewModel();

        vm.SelectedTimeoutActionIndex = AppPreferenceIndexPolicy.NoSelectionIndex;

        Assert.Equal(1, vm.SelectedTimeoutActionIndex);
        Assert.Equal(1, AppPreferences.Current.TimeoutActionIndex);
    }
}
