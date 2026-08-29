using System.Xml.Linq;
using Xunit;

namespace App.Tests;

// Task 11:验证所有冲突控件(同步/新增/保存/删除/添加附件)在 Vault 忙碌时被禁用,
// 且页面存在唯一 busy live region(ProgressRing + 文案,AutomationProperties.LiveSetting=Polite)。
public sealed class VaultPageBusyXamlTests
{
    [Theory]
    [InlineData("VaultSyncButton")]
    [InlineData("AddCipherButton")]
    [InlineData("SaveCipherEditorButton")]
    [InlineData("DeleteCipherButton")]
    [InlineData("AddAttachmentButton")]
    public void ConflictingButtons_AreDisabledWhileVaultBusy(string name)
    {
        var element = Load().Descendants().Single(node =>
            node.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == name)
            || node.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.AutomationId"
                && attribute.Value == name));
        Assert.Contains(
            "ViewModel.CanStartVaultOperation",
            element.Attribute("IsEnabled")?.Value);
    }

    [Fact]
    public void PageHasBusyLiveRegion()
    {
        var panel = Load().Descendants().Single(node =>
            node.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "VaultBusyPanel"));
        Assert.Contains("ViewModel.IsBusy", panel.Attribute("Visibility")?.Value);
        Assert.Contains(panel.Descendants(), node =>
            node.Attribute("AutomationProperties.LiveSetting")?.Value == "Polite");
    }

    [Fact]
    public void BusyLiveRegion_TextBoundToBusyMessage()
    {
        var panel = Load().Descendants().Single(node =>
            node.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "VaultBusyPanel"));
        var text = panel.Descendants().Single(node => node.Name.LocalName == "TextBlock");
        Assert.Contains("ViewModel.BusyMessage", text.Attribute("Text")?.Value);
    }

    [Fact]
    public void BusyLiveRegion_RemainsOutsideResponsivePanes()
    {
        var document = Load();
        var panel = document.Descendants().Single(node =>
            node.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "VaultBusyPanel"));
        var layoutRoot = document.Descendants().Single(node =>
            node.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "VaultLayoutRoot"));

        Assert.Same(layoutRoot, panel.Parent);
        Assert.Equal("0", panel.Attribute("Grid.Row")?.Value);
        Assert.Equal("2", panel.Attribute("Grid.ColumnSpan")?.Value);
        Assert.DoesNotContain(panel.Ancestors(), node =>
            node.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value is "VaultListPane" or "VaultDetailPane"));

        foreach (var paneName in new[] { "VaultListPane", "VaultDetailPane" })
        {
            var pane = document.Descendants().Single(node =>
                node.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name"
                    && attribute.Value == paneName));
            Assert.Equal("1", pane.Attribute("Grid.Row")?.Value);
        }
    }

    private static XDocument Load()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(
                directory.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path))
                return XDocument.Load(path);
        }
        throw new FileNotFoundException("VaultPage.xaml");
    }
}
