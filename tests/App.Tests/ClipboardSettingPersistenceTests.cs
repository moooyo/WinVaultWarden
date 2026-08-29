using App.Services;
using App.ViewModels;
using Xunit;

namespace App.Tests;

[Collection(TestCollectionNames.AppPreferences)]
public class ClipboardSettingPersistenceTests : AppPreferencesTestBase
{
    [Fact]
    public void ClearClipboardIndex_Persists_To_AppPreferences()
    {
        var vm = new SettingsViewModel();
        vm.SelectedClearClipboardIndex = 5;                 // 2 分钟
        Assert.Equal(5, AppPreferences.Current.ClearClipboardIndex);
    }

    [Fact]
    public void ClearClipboardIndex_Initializes_From_AppPreferences()
    {
        AppPreferences.Current.ClearClipboardIndex = 1;     // 10 秒
        var vm = new SettingsViewModel();
        Assert.Equal(1, vm.SelectedClearClipboardIndex);
    }

    [Fact]
    public void ClearClipboardIndex_InvalidSetter_NormalizesToDefault()
    {
        var vm = new SettingsViewModel();

        vm.SelectedClearClipboardIndex = 7;

        Assert.Equal(AppPreferenceIndexPolicy.DefaultClearClipboardIndex, vm.SelectedClearClipboardIndex);
        Assert.Equal(AppPreferenceIndexPolicy.DefaultClearClipboardIndex, AppPreferences.Current.ClearClipboardIndex);
    }

    [Fact]
    public void ClearClipboardIndex_TransientNoSelection_PreservesCurrentValue()
    {
        AppPreferences.Current.ClearClipboardIndex = 5;
        var vm = new SettingsViewModel();

        vm.SelectedClearClipboardIndex = AppPreferenceIndexPolicy.NoSelectionIndex;

        Assert.Equal(5, vm.SelectedClearClipboardIndex);
        Assert.Equal(5, AppPreferences.Current.ClearClipboardIndex);
    }
}
