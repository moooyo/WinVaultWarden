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
    public void List_UsesGroupedCollectionViewSource_WithStickyHeaders()
    {
        var doc = LoadVaultPageXaml();

        var cvs = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CollectionViewSource");
        Assert.NotNull(cvs);
        Assert.Equal("True", cvs!.Attribute("IsSourceGrouped")?.Value);
        // 分组对象(VaultListGroup)自身即条目集合,不再用反射式 ItemsPath —— AOT 下反射读属性会抛
        // NotSupportedException。故 ItemsPath 必须缺省。
        Assert.Null(cvs.Attribute("ItemsPath"));

        Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "ItemsStackPanel"
            && e.Attribute("AreStickyGroupHeadersEnabled")?.Value == "True");
        Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "GroupStyle");
    }
}
