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

    [Fact]
    public void Vault_SelectionMode_HasEntryButtonAndListSelectionSync()
    {
        var document = LoadXaml();

        // 「选择」入口按钮:进入选择模式(此前只有「完成」退出、无入口,多选整段在发布 UI 不可达)。
        var enter = document.Descendants(Xaml + "AppBarButton")
            .Single(b => b.Attribute("Label")?.Value == "选择");
        Assert.Contains("ToggleSelectionModeCommand", enter.Attribute("Command")?.Value ?? "");

        // CipherList 接线 SelectionChanged,把勾选精确同步进 VM 的选中集(R4)。
        var list = document.Descendants(Xaml + "ListView")
            .Single(e => e.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name")?.Value == "CipherList");
        Assert.Equal("OnCipherSelectionChanged", list.Attribute("SelectionChanged")?.Value);
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
