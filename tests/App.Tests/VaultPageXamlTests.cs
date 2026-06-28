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
        Assert.Contains("MoveSelectedToFolderCommand", move.Attribute("Command")?.Value ?? "");
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
