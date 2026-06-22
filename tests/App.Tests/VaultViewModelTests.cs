using App.Services;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class MockVaultUiServiceTests
{
    [Fact]
    public void GetItems_ReturnsFiveItems_OnePerKind()
    {
        var svc = new MockVaultUiService();
        var items = svc.GetItems();

        Assert.Equal(5, items.Count);
        Assert.Contains(items, i => i.Kind == VaultItemKind.Login);
        Assert.Contains(items, i => i.Kind == VaultItemKind.Card);
        Assert.Contains(items, i => i.Kind == VaultItemKind.Identity);
        Assert.Contains(items, i => i.Kind == VaultItemKind.Note);
        Assert.Contains(items, i => i.Kind == VaultItemKind.Ssh);
    }

    [Fact]
    public void GetDetail_LoginItem_ReturnsLoginDetailWithFields()
    {
        var svc = new MockVaultUiService();
        var detail = svc.GetDetail("1");

        var login = Assert.IsType<LoginDetail>(detail);
        Assert.Equal("百度网盘", login.Name);
        Assert.Equal("admin", login.Username);
        Assert.Equal("https://www.baidu.com", login.Uri);
    }

    [Fact]
    public void GetFilters_IncludesAllItemsAndFiveTypes()
    {
        var svc = new MockVaultUiService();
        var filters = svc.GetFilters();

        Assert.Contains(filters, f => f.Kind == FilterKind.AllItems);
        Assert.Equal(5, filters.Count(f => f.Kind == FilterKind.Type));
    }
}
