using System.Xml.Linq;
using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public sealed class VaultArchiveTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public async Task ArchiveAndUnarchive_MoveItemBetweenAllItemsAndArchiveViews()
    {
        var service = new MockVaultUiService();
        var viewModel = new VaultViewModel(service);
        var item = viewModel.FilteredItems.First(candidate => !candidate.IsDeleted);

        var archived = await viewModel.ArchiveAsync(item.Id);

        Assert.True(archived.IsSuccess);
        Assert.DoesNotContain(viewModel.FilteredItems, candidate => candidate.Id == item.Id);
        Assert.Equal(1, viewModel.Filters.Single(filter => filter.Kind == FilterKind.Archive).Count);

        viewModel.SelectFilterByTag("vault:archive");
        var archivedItem = Assert.Single(viewModel.FilteredItems, candidate => candidate.Id == item.Id);
        Assert.True(archivedItem.IsArchived);
        Assert.True(viewModel.IsArchiveFilterSelected);

        var unarchived = await viewModel.UnarchiveAsync(item.Id);

        Assert.True(unarchived.IsSuccess);
        Assert.Empty(viewModel.FilteredItems);
        Assert.True(viewModel.IsArchiveEmpty);
        viewModel.SelectFilterByTag("vault:allitems");
        Assert.Contains(viewModel.FilteredItems, candidate => candidate.Id == item.Id);
    }

    [Fact]
    public async Task BatchArchiveAndUnarchive_UseSelectionSpecificCapabilities()
    {
        var viewModel = new VaultViewModel(new MockVaultUiService())
        {
            IsSelectionMode = true,
        };
        var ids = viewModel.FilteredItems.Take(2).Select(item => item.Id).ToArray();
        foreach (var id in ids)
            viewModel.SetItemSelected(id, selected: true);

        Assert.True(viewModel.CanArchiveSelection);
        Assert.False(viewModel.CanUnarchiveSelection);
        await viewModel.ArchiveSelectedCommand.ExecuteAsync(null);

        viewModel.SelectFilterByTag("vault:archive");
        Assert.Equal(ids.Order(), viewModel.FilteredItems.Select(item => item.Id).Order());
        viewModel.IsSelectionMode = true;
        foreach (var id in ids)
            viewModel.SetItemSelected(id, selected: true);

        Assert.False(viewModel.CanArchiveSelection);
        Assert.True(viewModel.CanUnarchiveSelection);
        await viewModel.UnarchiveSelectedCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsArchiveEmpty);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    public void ViewModel_ArchiveCapabilitiesAreOperationSpecific(
        bool supportsProtocol,
        bool hasEntitlement,
        bool canArchive,
        bool canUnarchive)
    {
        var service = new MockVaultUiService(
            new Core.Models.VaultCapabilities(false, supportsProtocol, hasEntitlement));
        var viewModel = new VaultViewModel(service);

        Assert.Equal(canArchive, viewModel.CanArchiveItems);
        Assert.Equal(canUnarchive, viewModel.CanUnarchiveItems);
        Assert.All(viewModel.Items, item => Assert.Equal(canArchive, item.CanArchive));
        Assert.All(viewModel.Items, item => Assert.Equal(canUnarchive, item.CanUnarchive));
    }

    [Fact]
    public async Task ViewModel_UnsupportedArchiveCommandDoesNotCallService()
    {
        var service = new MockVaultUiService(Core.Models.VaultCapabilities.None);
        var viewModel = new VaultViewModel(service);
        var item = viewModel.FilteredItems.First(candidate => !candidate.IsDeleted);

        var result = await viewModel.ArchiveAsync(item.Id);

        Assert.False(result.IsSuccess);
        Assert.False(service.GetItems().Single(candidate => candidate.Id == item.Id).IsArchived);
    }

    [Fact]
    public void ViewModel_CapabilityRefreshAndClearUpdateDynamicActions()
    {
        var service = new MockVaultUiService(new Core.Models.VaultCapabilities(true, true, true));
        var viewModel = new VaultViewModel(service);

        Assert.True(viewModel.CanCreateNewCipherTypes);
        viewModel.BeginAdd(VaultItemKind.BankAccount);
        Assert.True(viewModel.CanSaveEditorDraft);

        service.Capabilities = Core.Models.VaultCapabilities.None;
        viewModel.RefreshFromSnapshot();

        Assert.False(viewModel.CanCreateNewCipherTypes);
        Assert.False(viewModel.CanSaveEditorDraft);
        viewModel.ClearSensitive();
        Assert.Equal(Core.Models.VaultCapabilities.None, viewModel.Capabilities);
    }

    [Fact]
    public void VaultXaml_ExposesArchiveNavigationAndActions()
    {
        var mainWindow = LoadXaml("src", "App", "MainWindow.xaml");
        var vaultPage = LoadXaml("src", "App", "Views", "VaultPage.xaml");

        Assert.Contains(mainWindow.Descendants(), element => element.Attribute("Tag")?.Value == "vault:archive");
        Assert.Contains(vaultPage.Descendants(Xaml + "MenuFlyoutItem"),
            element => element.Attribute("Click")?.Value == "OnRowArchiveClick");
        Assert.Contains(vaultPage.Descendants(Xaml + "MenuFlyoutItem"),
            element => element.Attribute("Click")?.Value == "OnRowUnarchiveClick");
        Assert.Contains(vaultPage.Descendants(Xaml + "MenuFlyoutItem"),
            element => element.Attribute("Click")?.Value == "OnArchiveCipherClick");
        Assert.Contains(vaultPage.Descendants(Xaml + "MenuFlyoutItem"),
            element => element.Attribute("Click")?.Value == "OnUnarchiveCipherClick");
    }

    private static XDocument LoadXaml(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(path))
                return XDocument.Load(path);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {string.Join('/', relativePath)} from the test output directory.");
    }
}
