using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class FieldRowXamlTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Controls", "FieldRow.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("FieldRow.xaml not found.");
    }

    [Fact]
    public void RevealButton_ExplainsRequiredVerification()
    {
        var doc = Load();
        var reveal = doc.Descendants().FirstOrDefault(e =>
            e.Attribute(X + "Name")?.Value == "RevealButton");

        Assert.NotNull(reveal);
        Assert.Equal("显示", reveal!.Attribute("AutomationProperties.Name")?.Value);
        Assert.Equal("显示（需 Windows Hello）", reveal.Attribute("ToolTipService.ToolTip")?.Value);
    }

    [Fact]
    public void CopyButton_HasNameAndContextualAutomationName()
    {
        var document = Load();
        var copy = document.Descendants().Single(element =>
            element.Attribute(X + "Name")?.Value == "CopyButton");
        Assert.Equal("字段，复制", copy.Attribute("AutomationProperties.Name")?.Value);

        var source = ReadCodeBehind();
        Assert.Contains("$\"复制{label}\"", source);
        Assert.Contains("$\"已复制{label}\"", source);
        Assert.Contains("$\"{(_revealed ? \"隐藏\" : \"显示\")}{label}\"", source);
    }

    [Fact]
    public void FieldRow_UsesFlatLabelValueAndActionColumns()
    {
        var document = Load();
        var border = document.Descendants().Single(element => element.Attribute(X + "Name")?.Value == "RowBorder");
        var label = document.Descendants().Single(element => element.Attribute(X + "Name")?.Value == "LabelColumn");
        var actions = document.Descendants().Single(element => element.Attribute(X + "Name")?.Value == "ActionsPanel");

        Assert.Equal("0,0,0,1", border.Attribute("BorderThickness")?.Value);
        Assert.Null(border.Attribute("Background"));
        Assert.Equal("112", label.Attribute("Width")?.Value);
        Assert.Equal("2", actions.Attribute("Grid.Column")?.Value);
        Assert.Contains(document.Descendants(), element => element.Attribute(X + "Name")?.Value == "CopyFeedback"
            && element.Attribute("AutomationProperties.LiveSetting")?.Value == "Polite");
    }

    private static string ReadCodeBehind()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(
                directory.FullName,
                "src", "App", "Controls", "FieldRow.xaml.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException("FieldRow.xaml.cs");
    }
}
