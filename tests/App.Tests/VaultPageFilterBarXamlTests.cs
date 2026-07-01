using Xunit;

namespace App.Tests;

public class VaultPageFilterBarXamlTests
{
    private static string LoadText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("VaultPage.xaml not found.");
    }

    [Fact]
    public void FacetTotp_BoundTwoWay()
    {
        var xaml = LoadText();
        Assert.Contains("IsChecked=\"{x:Bind ViewModel.FacetTotp, Mode=TwoWay}\"", xaml);
    }

    [Fact]
    public void FacetAttachment_BoundTwoWay()
    {
        var xaml = LoadText();
        Assert.Contains("IsChecked=\"{x:Bind ViewModel.FacetAttachment, Mode=TwoWay}\"", xaml);
    }

    [Fact]
    public void FacetUri_BoundTwoWay()
    {
        var xaml = LoadText();
        Assert.Contains("IsChecked=\"{x:Bind ViewModel.FacetUri, Mode=TwoWay}\"", xaml);
    }

    [Fact]
    public void FacetFavoriteOnly_BoundTwoWay()
    {
        var xaml = LoadText();
        Assert.Contains("IsChecked=\"{x:Bind ViewModel.FacetFavoriteOnly, Mode=TwoWay}\"", xaml);
    }

    [Fact]
    public void SortComboBox_BoundToSelectedSortIndex_TwoWay()
    {
        var xaml = LoadText();
        Assert.Contains("SelectedIndex=\"{x:Bind ViewModel.SelectedSortIndex, Mode=TwoWay}\"", xaml);
    }

    [Fact]
    public void SavedViews_BoundInFlyout()
    {
        var xaml = LoadText();
        Assert.Contains("SavedViewsFlyout", xaml);
        Assert.Contains("OnSavedViewsFlyoutOpening", xaml);
    }

    [Fact]
    public void ApplyViewCommand_InvokedFromCodeBehind()
    {
        var xaml = LoadText();
        // ApplyViewCommand 走 code-behind(MenuFlyoutItem.Click 动态构建),XAML 层可见的是触发入口。
        Assert.Contains("SavedViewsButton", xaml);
    }

    [Fact]
    public void HasActiveRefinement_ControlsClearFiltersVisibility()
    {
        var xaml = LoadText();
        Assert.Contains("Visibility=\"{x:Bind ViewModel.HasActiveRefinement, Mode=OneWay, Converter={StaticResource BoolToVis}}\"", xaml);
    }

    [Fact]
    public void SaveCurrentViewButton_HasClickHandler()
    {
        var xaml = LoadText();
        Assert.Contains("Click=\"OnSaveCurrentViewClick\"", xaml);
    }

    [Fact]
    public void ClearFiltersButton_HasClickHandler()
    {
        var xaml = LoadText();
        Assert.Contains("Click=\"OnClearFiltersClick\"", xaml);
    }

    [Fact]
    public void FacetToggles_HaveAutomationIds()
    {
        var xaml = LoadText();
        Assert.Contains("AutomationProperties.AutomationId=\"VaultFacetTotpToggle\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"VaultFacetAttachmentToggle\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"VaultFacetUriToggle\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"VaultFacetFavoriteOnlyToggle\"", xaml);
    }
}
