using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class VaultRowActionsXamlTests
{
    private static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("VaultPage.xaml not found.");
    }

    [Fact]
    public void RowActions_PanelExists_DefaultTransparent()
    {
        var doc = Load();
        var row = doc.Descendants().Single(e =>
            e.Attribute("PointerEntered")?.Value == "OnRowPointerEntered");
        var panel = doc.Descendants().FirstOrDefault(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "RowActions"));
        Assert.NotNull(panel);
        Assert.Equal("0", panel!.Attribute("Opacity")?.Value);
        Assert.Equal("False", panel.Attribute("IsHitTestVisible")?.Value);
        Assert.Equal("OnRowPointerExited", row.Attribute("PointerExited")?.Value);
        Assert.Equal("OnRowGotFocus", row.Attribute("GotFocus")?.Value);
        Assert.Equal("OnRowLostFocus", row.Attribute("LostFocus")?.Value);
        Assert.Equal("OnRowUnloaded", row.Attribute("Unloaded")?.Value);
    }

    [Fact]
    public void RowActions_UsePointerAndFocusWithinStateTogether()
    {
        var source = LoadCode();

        Assert.Contains("ConditionalWeakTable<Grid, RowActionState>", source, StringComparison.Ordinal);
        Assert.Contains("isPointerOver || isFocusWithin", source, StringComparison.Ordinal);
        Assert.Contains("state.IsPointerOver", source, StringComparison.Ordinal);
        Assert.Contains("state.IsFocusWithin", source, StringComparison.Ordinal);
        Assert.Contains("FocusManager.GetFocusedElement", source, StringComparison.Ordinal);
        Assert.Contains("_rowActionStates.Remove(root)", source, StringComparison.Ordinal);
        Assert.Contains("panel.IsHitTestVisible = show", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RowActionButtons_HaveAutomationNames()
    {
        var doc = Load();
        var names = doc.Descendants()
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName == "AutomationProperties.Name")
            .Select(a => a.Value)
            .ToList();
        Assert.Contains("复制", names);
        Assert.Contains("更多", names);
    }

    private static string LoadCode()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "VaultPage.xaml.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("VaultPage.xaml.cs not found.");
    }
}
