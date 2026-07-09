using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class VaultPageGroupingXamlTests
{
    private static XDocument LoadVaultPageXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path))
                return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("VaultPage.xaml not found.");
    }

    [Fact]
    public void List_UsesFlatFilteredItems_NoCollectionViewSourceGrouping()
    {
        // 回归:Native AOT 下 CollectionViewSource(IsSourceGrouped)只建组不渲染子项
        // (microsoft-ui-xaml #10881),再叠加 GroupStyle HidesIfEmpty 导致整份保险库列表不可见。
        // 因此列表改为平铺绑定 ViewModel.FilteredItems(纯 x:Bind,AOT 安全,必然渲染)。
        var doc = LoadVaultPageXaml();

        // 不得再出现分组用的 CollectionViewSource。
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "CollectionViewSource");
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "GroupStyle");

        // CipherList 直接绑定平铺的 FilteredItems。
        var list = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "ListView" && e.Attribute(Xaml.X + "Name")?.Value == "CipherList");
        Assert.NotNull(list);
        Assert.Contains("ViewModel.FilteredItems", list!.Attribute("ItemsSource")?.Value ?? "");
    }
}

internal static class Xaml
{
    public static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
}
