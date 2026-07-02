using App.Services;
using App.ViewModels;
using Xunit;

namespace App.Tests;

public class ClipboardSettingPersistenceTests
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
}
