using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class VaultGroupingTests
{
    private static VaultViewModel NewVm() => new(new MockVaultUiService());

    [Fact]
    public void AllItems_DefaultsToRecentUseGroupsWithoutTypeBoundaries()
    {
        var vm = NewVm();
        vm.SelectFilterByTag("vault:allitems");

        Assert.Equal(VaultSortKey.RecentUse, vm.SelectedSort);
        var group = Assert.Single(vm.GroupedItems);
        Assert.True(group.ShowHeader);
        Assert.Equal("更早", group.Key);
        Assert.True(group.Select(item => item.Kind).Distinct().Count() > 1);
        Assert.Equal(vm.FilteredItems.Count, vm.GroupedItems.Sum(g => g.Count));
        AssertInlineHeaders(vm);
    }

    [Fact]
    public void ByType_KeepsRecentUseGroupingAcrossFolders()
    {
        var vm = NewVm();
        vm.SelectFilterByTag("vault:type:Login");

        var group = Assert.Single(vm.GroupedItems);
        Assert.True(group.ShowHeader);
        Assert.Equal("更早", group.Key);
        Assert.All(group, item => Assert.Equal(VaultItemKind.Login, item.Kind));
        Assert.Equal(vm.FilteredItems, group);
        AssertInlineHeaders(vm);
    }

    [Fact]
    public void ByFolder_KeepsTheRecentUseHeader()
    {
        var vm = NewVm();
        vm.SelectFilterByTag("vault:folder:f1");

        Assert.Single(vm.GroupedItems);
        Assert.True(vm.GroupedItems[0].ShowHeader);
        Assert.Equal("更早", vm.GroupedItems[0].Key);
        Assert.Equal(vm.FilteredItems.Count, vm.GroupedItems[0].Count);
        AssertInlineHeaders(vm);
    }

    [Fact]
    public void Search_RemovesDateHeadersAndPreservesEveryResult()
    {
        var vm = NewVm();
        vm.SelectFilterByTag("vault:allitems");
        vm.SearchText = "百度";

        var group = Assert.Single(vm.GroupedItems);
        Assert.False(group.ShowHeader);
        Assert.Empty(group.Key);
        Assert.Equal(vm.FilteredItems, group);
        Assert.All(vm.FilteredItems, item => Assert.False(item.HasGroupHeader));
    }

    [Fact]
    public void RecentUseGroups_UseTodayPastSevenDaysAndEarlierInDescendingOrder()
    {
        var vm = new VaultViewModel(new FakeFacetVaultUiService(
        [
            new() { Id = "never", Name = "Never", Kind = VaultItemKind.Login, Favorite = true },
            new() { Id = "old", Name = "Older", Kind = VaultItemKind.Card, Favorite = true },
            new() { Id = "week", Name = "This week", Kind = VaultItemKind.Note, Favorite = true },
            new() { Id = "today", Name = "Today", Kind = VaultItemKind.Identity, Favorite = true },
        ]));
        var today = new DateTimeOffset(DateTime.Today);
        vm.Items.Single(item => item.Id == "old").LastUsedAt = today.AddDays(-7);
        vm.Items.Single(item => item.Id == "week").LastUsedAt = today.AddDays(-6);
        vm.Items.Single(item => item.Id == "today").LastUsedAt = today;

        // Refine the current snapshot without triggering the search refresh boundary.
        vm.FacetFavoriteOnly = true;

        Assert.Equal(new[] { "今天", "过去 7 天", "更早" }, vm.GroupedItems.Select(group => group.Key));
        Assert.Equal(new[] { "today", "week", "old", "never" }, vm.FilteredItems.Select(item => item.Id));
        Assert.All(vm.GroupedItems, group => Assert.True(group.ShowHeader));
        Assert.Equal(vm.FilteredItems, vm.GroupedItems.SelectMany(group => group));
        Assert.Equal(new[] { "今天", "过去 7 天", "更早", "" }, vm.FilteredItems.Select(item => item.GroupHeader));
        AssertInlineHeaders(vm);
    }

    [Fact]
    public void ChangingSort_ClearsInlineHeadersAndRestoresThemForRecentUse()
    {
        var vm = NewVm();
        AssertInlineHeaders(vm);

        vm.SelectedSort = VaultSortKey.NameAsc;

        Assert.All(vm.FilteredItems, item => Assert.False(item.HasGroupHeader));
        Assert.False(Assert.Single(vm.GroupedItems).ShowHeader);
        vm.SelectedSort = VaultSortKey.RecentUse;
        AssertInlineHeaders(vm);
    }

    [Fact]
    public void SelectionMode_ClearsAndRestoresInlineHeadersWithoutRefreshingUsageOrder()
    {
        var vm = NewVm();
        var first = vm.FilteredItems[0];
        var originalOrder = vm.FilteredItems.Select(item => item.Id).ToArray();
        var headerChanges = new List<string?>();
        first.PropertyChanged += (_, args) => headerChanges.Add(args.PropertyName);
        vm.RecordRecentUse(vm.FilteredItems[^1].Id);
        Assert.True(first.HasGroupHeader);

        vm.IsSelectionMode = true;

        Assert.All(vm.FilteredItems, item => Assert.False(item.HasGroupHeader));
        Assert.False(Assert.Single(vm.GroupedItems).ShowHeader);
        Assert.Contains(nameof(CipherListItem.GroupHeader), headerChanges);
        Assert.Contains(nameof(CipherListItem.HasGroupHeader), headerChanges);
        Assert.Equal(originalOrder, vm.FilteredItems.Select(item => item.Id));
        vm.IsSelectionMode = false;
        Assert.Equal(originalOrder, vm.FilteredItems.Select(item => item.Id));
        AssertInlineHeaders(vm);
    }

    private static void AssertInlineHeaders(VaultViewModel vm)
    {
        Assert.NotEmpty(vm.GroupedItems);
        foreach (var group in vm.GroupedItems)
        {
            Assert.True(group.ShowHeader);
            Assert.Equal(group.Key, group[0].GroupHeader);
            Assert.True(group[0].HasGroupHeader);
            Assert.All(group.Skip(1), item =>
            {
                Assert.Empty(item.GroupHeader);
                Assert.False(item.HasGroupHeader);
            });
        }
        Assert.Equal(vm.GroupedItems.Count, vm.FilteredItems.Count(item => item.HasGroupHeader));
    }
}
