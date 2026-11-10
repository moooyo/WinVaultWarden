using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class VaultPageBindingXamlTests
{
    private static string Xaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find VaultPage.xaml from the test output directory.");
    }

    [Fact]
    public void No_classic_ElementName_Root_bindings_remain()
    {
        // 全量 AOT 要求 0 处反射 {Binding};VaultPage 是最后的留存点。
        Assert.DoesNotContain("{Binding ElementName=Root", Xaml());
    }

    [Fact]
    public void SelectionToolbar_HasBulkActionBindings()
    {
        var xaml = Xaml();
        Assert.Contains("SoftDeleteSelectedCommand", xaml);
        Assert.Contains("RestoreSelectedCommand", xaml);
        Assert.Contains("OnPermanentDeleteSelectedClick", xaml);   // 永久删走 code-behind 确认
        Assert.Contains("IsTrashFilterSelected", xaml);            // 回收站上下文可见性
    }

    [Fact]
    public void MoveButton_UsesMenuFlyout_NotHardcodedNull()
    {
        var xaml = Xaml();
        // 移动按钮挂 flyout（由 code-behind 依 FolderFilters 动态填充），不再写死 CommandParameter="{x:Null}" 作为唯一入口
        Assert.Contains("OnMoveSelectedFlyoutOpening", xaml);
    }

    [Fact]
    public void VaultPage_UsesFaviconViewForListAndDetail()
    {
        var xaml = Xaml();
        Assert.Contains("FaviconView", xaml);
        Assert.Contains("IconDomain", xaml); // 列表项绑定
    }

    [Fact]
    public void LoginTemplate_HasPasswordHistorySummaryAndDialogAction()
    {
        var document = XDocument.Parse(Xaml());
        var template = document.Descendants().Single(element => element.Name.LocalName == "DataTemplate"
            && element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Key")?.Value == "LoginTemplate");
        var history = template.Descendants().Single(element => element.Name.LocalName == "Grid"
            && element.Attribute("Visibility")?.Value?.Contains("HasPasswordHistory") == true);
        Assert.Contains(history.Descendants(), element => element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value == "{Binding PasswordHistorySummary}");
        Assert.Contains(history.Descendants(), element => element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value == "{Binding LatestPasswordHistoryText}");
        Assert.Contains(history.Descendants(), element => element.Name.LocalName == "Button"
            && element.Attribute("Click")?.Value == "OnPasswordHistoryClick"
            && element.Attribute("AutomationProperties.Name")?.Value == "查看密码历史");
        Assert.DoesNotContain(template.Descendants(), element => element.Name.LocalName == "Expander");
        Assert.DoesNotContain(template.Descendants(), element => element.Name.LocalName == "ItemsControl"
            && element.Attribute("ItemsSource")?.Value == "{Binding PasswordHistory}");
    }
}
