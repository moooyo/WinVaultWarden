using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class VaultPageXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Theory]
    [InlineData("FacetFilterButton", "\uE71C", "VaultFacetFilterButton")]
    [InlineData("SavedViewsButton", "\uE8A9", "VaultSavedViewsButton")]
    public void CompactFlyoutButtons_KeepTheirIconsAndActionsReachable(
        string name, string glyph, string automationId)
    {
        var document = LoadXaml();
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var button = document.Descendants().Single(element => element.Attribute(x + "Name")?.Value == name);

        // A native DropDownButton chevron consumed these 30-pixel icon surfaces in the walkthrough.
        Assert.Equal(Xaml + "Button", button.Name);
        Assert.Equal(automationId, button.Attribute("AutomationProperties.AutomationId")?.Value);
        var content = button.Elements(Xaml + "StackPanel").Single();
        Assert.Equal(glyph, content.Elements(Xaml + "FontIcon").Single().Attribute("Glyph")?.Value);
        var flyout = button.Element(Xaml + "Button.Flyout");
        Assert.NotNull(flyout);
        if (name == "FacetFilterButton")
        {
            foreach (var property in new[] { "FacetTotp", "FacetAttachment", "FacetUri", "FacetFavoriteOnly" })
                Assert.Contains(flyout!.Descendants(Xaml + "ToggleButton"), element =>
                    element.Attribute("IsChecked")?.Value == $"{{x:Bind ViewModel.{property}, Mode=TwoWay}}");
        }
        else
        {
            Assert.Contains(flyout!.Descendants(Xaml + "MenuFlyout"), element =>
                element.Attribute("Opening")?.Value == "OnSavedViewsFlyoutOpening");
            Assert.Contains(flyout.Descendants(Xaml + "MenuFlyoutItem"), element =>
                element.Attribute("Click")?.Value == "OnSaveCurrentViewClick");
        }
    }

    [Fact]
    public void Vault_SelectAndMoveButtons_BindToCommands()
    {
        var document = LoadXaml();
        var buttons = document.Descendants(Xaml + "AppBarButton").ToList();

        var selectAll = buttons.Single(b => b.Attribute("Label")?.Value == "全选");
        var move = buttons.Single(b => b.Attribute("Label")?.Value == "移动");

        Assert.Contains("SelectAllCommand", selectAll.Attribute("Command")?.Value ?? "");
        // Folder actions are populated dynamically when the flyout opens.
        Assert.NotNull(move.Element(Xaml + "AppBarButton.Flyout"));
        Assert.Null(move.Attribute("Command"));
    }

    [Fact]
    public void Vault_CipherList_BindsSelectionMode()
    {
        var document = LoadXaml();
        var list = document.Descendants(Xaml + "ListView")
            .Single(e => e.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name")?.Value == "CipherList");

        Assert.Contains("SelectionModeFromBool", list.Attribute("SelectionMode")?.Value ?? "");
    }

    [Fact]
    public void Vault_SelectionMode_HasEntryButtonAndListSelectionSync()
    {
        var document = LoadXaml();

        // Selection mode must remain reachable from the command bar.
        var enter = document.Descendants(Xaml + "AppBarButton")
            .Single(b => b.Attribute("Label")?.Value == "选择");
        Assert.Contains("ToggleSelectionModeCommand", enter.Attribute("Command")?.Value ?? "");

        // Keep the visual selection synchronized with the view model.
        var list = document.Descendants(Xaml + "ListView")
            .Single(e => e.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name")?.Value == "CipherList");
        Assert.Equal("OnCipherSelectionChanged", list.Attribute("SelectionChanged")?.Value);
    }

    [Fact]
    public void Vault_OrganizationPlacement_HasDetailActionsAndEditorCollectionCheckboxes()
    {
        var document = LoadXaml();
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        var transfer = document.Descendants(Xaml + "MenuFlyoutItem")
            .Single(button => button.Attribute(x + "Name")?.Value == "TransferCipherToOrganizationButton");
        var manage = document.Descendants(Xaml + "MenuFlyoutItem")
            .Single(button => button.Attribute(x + "Name")?.Value == "ManageCipherCollectionsButton");
        var collectionCheckbox = document.Descendants(Xaml + "CheckBox")
            .Single(box => (box.Attribute("IsChecked")?.Value ?? "").Contains("IsSelected"));

        Assert.Equal("OnTransferCipherToOrganizationClick", transfer.Attribute("Click")?.Value);
        Assert.Equal("OnManageCipherCollectionsClick", manage.Attribute("Click")?.Value);
        Assert.Contains("CanTransferSelectedItem", transfer.Attribute("Visibility")?.Value ?? "");
        Assert.Contains("CanManageSelectedItemCollections", manage.Attribute("Visibility")?.Value ?? "");
        Assert.Contains("CanSelect", collectionCheckbox.Attribute("IsEnabled")?.Value ?? "");
    }

    [Fact]
    public void Vault_OrganizationPlacementDialog_LocksReadOnlyAssignments()
    {
        var code = LoadCodeBehind();

        Assert.Contains("lockReadOnlyAssignments: true", code, StringComparison.Ordinal);
        Assert.Contains("IsEnabled = option.CanSelect", code, StringComparison.Ordinal);
        Assert.Contains("请选择至少一个集合", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Vault_PendingCreateAbandonment_IsSessionBoundAndNeverResubmitsDraft()
    {
        var code = LoadCodeBehind();
        var start = code.IndexOf(
            "private async void OnAbandonPendingCipherCreateClick",
            StringComparison.Ordinal);
        var end = code.IndexOf(
            "private void OnCipherEditorTypeChanged",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var handler = code[start..end];

        Assert.Contains("CapturePendingCipherCreate()", handler, StringComparison.Ordinal);
        Assert.Contains(".CaptureLease()", handler, StringComparison.Ordinal);
        Assert.Contains("DialogHelper.ConfirmAsync(", handler, StringComparison.Ordinal);
        Assert.Contains("lease,", handler, StringComparison.Ordinal);
        Assert.Contains("AbandonPendingCipherCreateAsync(expected)", handler, StringComparison.Ordinal);
        Assert.Contains("服务器已经返回成功", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveDraftAsync", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void Vault_PasswordExposureFindingIsClearedOnlyAfterSuccessfulPasswordSave()
    {
        var code = LoadCodeBehind();
        var start = code.IndexOf("private async void OnSaveCipherEditorClick", StringComparison.Ordinal);
        var end = code.IndexOf("private async void OnAbandonPendingCipherCreateClick", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var handler = code[start..end];
        var capture = handler.IndexOf("var passwordChanged = ViewModel.HasEditorPasswordChange", StringComparison.Ordinal);
        var save = handler.IndexOf("await ViewModel.SaveDraftAsync()", StringComparison.Ordinal);
        Assert.True(capture >= 0 && save > capture);

        var successStart = handler.IndexOf("if (result.IsSuccess)", save, StringComparison.Ordinal);
        var successEnd = handler.IndexOf("else if", successStart, StringComparison.Ordinal);
        Assert.True(successStart > save && successEnd > successStart);
        var success = handler[successStart..successEnd];
        Assert.Contains("if (passwordChanged && !string.IsNullOrEmpty(result.Value))", success);
        Assert.Contains("GetRequiredService<SecurityReportViewModel>().MarkPasswordChanged(result.Value)", success);
        Assert.Equal(1, handler.Split(".MarkPasswordChanged(", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("MarkPasswordChanged", handler[..successStart]);
        Assert.DoesNotContain("MarkPasswordChanged", handler[successEnd..]);
    }

    private static XDocument LoadXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path))
                return XDocument.Load(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find VaultPage.xaml from the test output directory.");
    }

    private static string LoadCodeBehind()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "VaultPage.xaml.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find VaultPage.xaml.cs from the test output directory.");
    }
}
