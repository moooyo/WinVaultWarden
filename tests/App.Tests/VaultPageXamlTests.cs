using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class VaultPageXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void Vault_SelectAndMoveButtons_BindToCommands()
    {
        var document = LoadXaml();
        var buttons = document.Descendants(Xaml + "AppBarButton").ToList();

        var selectAll = buttons.Single(b => b.Attribute("Label")?.Value == "全选");
        var move = buttons.Single(b => b.Attribute("Label")?.Value == "移动");

        Assert.Contains("SelectAllCommand", selectAll.Attribute("Command")?.Value ?? "");
        // 移动按钮改为 MenuFlyout 模式（OnMoveSelectedFlyoutOpening 在 code-behind 动态填充），
        // 不再把 MoveSelectedToFolderCommand 直接绑 Command 属性。
        // 验证按钮有 Flyout（即 MenuFlyout 子元素）而非硬编码 CommandParameter。
        Assert.NotNull(move.Element(Xaml + "AppBarButton.Flyout"));
        Assert.Null(move.Attribute("Command")); // command 已移至 flyout items
    }

    [Fact]
    public void Vault_CipherList_BindsSelectionMode()
    {
        var document = LoadXaml();
        var list = document.Descendants(Xaml + "ListView")
            .Single(e => e.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name")?.Value == "CipherList");

        Assert.Contains("SelectionModeFromBool", list.Attribute("SelectionMode")?.Value ?? "");
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
}
