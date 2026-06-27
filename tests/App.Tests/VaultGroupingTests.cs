using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class VaultGroupingTests
{
    private static VaultViewModel NewVm() => new(new MockVaultUiService());

    [Fact]
    public void AllItems_GroupsByType_WithFixedOrder_AndShowsHeaders()
    {
        var vm = NewVm();
        vm.SelectFilterByTag("vault:allitems");

        Assert.NotEmpty(vm.GroupedItems);
        Assert.All(vm.GroupedItems, g => Assert.True(g.ShowHeader));
        var names = new[] { "登录", "银行卡", "身份", "笔记", "SSH 密钥" };
        Assert.All(vm.GroupedItems, g => Assert.Contains(g.Key, names));
        var order = vm.GroupedItems.Select(g => Array.IndexOf(names, g.Key)).ToList();
        Assert.Equal(order.OrderBy(x => x), order);
        Assert.Equal(vm.FilteredItems.Count, vm.GroupedItems.Sum(g => g.Count));
    }

    [Fact]
    public void ByType_GroupsByFolder_NoFolderLast()
    {
        var vm = NewVm();
        vm.SelectFilterByTag("vault:type:Login");

        Assert.NotEmpty(vm.GroupedItems);
        Assert.All(vm.GroupedItems, g => Assert.True(g.ShowHeader));
        foreach (var g in vm.GroupedItems)
            Assert.Single(g.Items.Select(i => i.FolderId).Distinct());
        Assert.Equal(vm.FilteredItems.Select(i => i.FolderId).Distinct().Count(), vm.GroupedItems.Count);
        var noFolder = vm.GroupedItems.FirstOrDefault(g => g.Key == "无文件夹");
        if (noFolder is not null)
            Assert.Same(noFolder, vm.GroupedItems[^1]);
    }

    [Fact]
    public void ByFolder_SingleGroup_HeaderHidden()
    {
        var vm = NewVm();
        vm.SelectFilterByTag("vault:folder:f1");

        Assert.Single(vm.GroupedItems);
        Assert.False(vm.GroupedItems[0].ShowHeader);
        Assert.Equal(vm.FilteredItems.Count, vm.GroupedItems[0].Count);
    }

    [Fact]
    public void Search_RebuildsGroups()
    {
        var vm = NewVm();
        vm.SelectFilterByTag("vault:allitems");
        vm.SearchText = "百度";

        Assert.Equal(vm.FilteredItems.Count, vm.GroupedItems.Sum(g => g.Count));
        Assert.All(vm.GroupedItems, g => Assert.NotEmpty(g.Items));
    }
}
