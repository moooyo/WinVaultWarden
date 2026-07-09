using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class GeneratorDialogXamlTests
{
    internal static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "GeneratorDialog.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("GeneratorDialog.xaml not found.");
    }

    private static string LoadCodeBehind()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "GeneratorDialog.xaml.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("GeneratorDialog.xaml.cs not found.");
    }

    [Fact]
    public void GeneratedValueTextBlocks_UseMonospaceResource_NoInlineConsolas()
    {
        var doc = Load();
        Assert.DoesNotContain(doc.Descendants(),
            e => e.Attribute("FontFamily")?.Value == "Consolas");

        var monoCount = doc.Descendants()
            .Count(e => e.Attribute("FontFamily")?.Value == "{StaticResource MonospaceFontFamily}");
        Assert.Equal(3, monoCount);
    }

    [Fact]
    public void ResultCardBorders_UseCardBorderStyle_AndCompactPadding()
    {
        var doc = Load();
        // 每个 Tab 的结果卡片仍是手写 Border(选项行已迁移到 SettingsCard),共 3 个。
        var styled = doc.Descendants()
            .Where(e => e.Name.LocalName == "Border"
                && e.Attribute("Style")?.Value == "{StaticResource CardBorderStyle}")
            .ToList();
        Assert.Equal(3, styled.Count);

        Assert.All(styled, e =>
            Assert.Equal("{StaticResource CardContentPaddingCompact}", e.Attribute("Padding")?.Value));

        // 选项行不再用整卡 Border 包裹,CardContentPadding(非 Compact)不应再出现。
        Assert.DoesNotContain(doc.Descendants(),
            e => e.Attribute("Padding")?.Value == "{StaticResource CardContentPadding}");
    }

    [Fact]
    public void UsesTkNamespace_ForSettingsCard()
    {
        var doc = Load();
        var tkNs = doc.Root?.Attributes()
            .FirstOrDefault(a => a.Name.LocalName == "tk" && a.Value == "using:CommunityToolkit.WinUI.Controls");
        Assert.NotNull(tkNs);
    }

    [Fact]
    public void OptionsUseMultipleSettingsCards()
    {
        var doc = Load();
        var cards = doc.Descendants().Where(e => e.Name.LocalName == "SettingsCard").ToList();
        // 密码(5) + 密码短语(4) + 用户名(3) + 历史记录入口(1) = 13
        Assert.Equal(13, cards.Count);
    }

    [Fact]
    public void StrengthMeter_ProgressBarBoundToStrengthFraction()
    {
        var doc = Load();
        var bars = doc.Descendants()
            .Where(e => e.Name.LocalName == "ProgressBar"
                && (e.Attribute("Value")?.Value.Contains("ViewModel.StrengthFraction") ?? false))
            .ToList();
        // 密码 + 密码短语 两个 Tab 各一个,用户名 Tab 不含强度条。
        Assert.Equal(2, bars.Count);
        Assert.All(bars, e => Assert.Contains("Mode=OneWay", e.Attribute("Value")!.Value));
    }

    [Fact]
    public void StrengthMeter_LabelBoundToStrengthLabel()
    {
        var doc = Load();
        var labels = doc.Descendants()
            .Where(e => e.Name.LocalName == "TextBlock"
                && (e.Attribute("Text")?.Value.Contains("ViewModel.StrengthLabel") ?? false))
            .ToList();
        Assert.Equal(2, labels.Count);
    }

    [Fact]
    public void StrengthPanel_VisibilityBoundToShowStrength()
    {
        var doc = Load();
        var panels = doc.Descendants()
            .Where(e => e.Attribute("Visibility")?.Value.Contains("ViewModel.ShowStrength") ?? false)
            .ToList();
        Assert.Equal(2, panels.Count);
        Assert.All(panels, e => Assert.Contains("Converter={StaticResource BoolToVis}", e.Attribute("Visibility")!.Value));
    }

    [Fact]
    public void UsernameTab_HasNoStrengthMeter()
    {
        var doc = Load();
        var usernameTab = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "TabViewItem"
                && (string?)e.Attribute("Header") == "用户名");
        Assert.NotNull(usernameTab);
        Assert.DoesNotContain(usernameTab!.Descendants(), e => e.Name.LocalName == "ProgressBar");
    }

    [Fact]
    public void AvoidAmbiguous_IsToggleSwitch_BoundTwoWay()
    {
        var doc = Load();
        var toggle = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ToggleSwitch"
                && (e.Attribute("IsOn")?.Value.Contains("ViewModel.AvoidAmbiguous") ?? false));
        Assert.NotNull(toggle);
        Assert.Contains("Mode=TwoWay", toggle!.Attribute("IsOn")!.Value);

        // 旧的 CheckBox 版本不应再存在。
        Assert.DoesNotContain(doc.Descendants(),
            e => e.Name.LocalName == "CheckBox"
                && (e.Attribute("IsChecked")?.Value.Contains("ViewModel.AvoidAmbiguous") ?? false));
    }

    [Fact]
    public void HistoryEntry_IsClickableSettingsCard_WithHandler()
    {
        var doc = Load();
        var card = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "SettingsCard"
                && (string?)e.Attribute("Header") == "生成器历史记录");
        Assert.NotNull(card);
        Assert.Equal("True", (string?)card!.Attribute("IsClickEnabled"));
        Assert.Equal("OnHistoryClick", (string?)card.Attribute("Click"));

        // 旧的手写 Button/Grid 历史记录行不应再存在。
        Assert.DoesNotContain(doc.Descendants(),
            e => e.Name.LocalName == "Button" && (string?)e.Attribute("Click") == "OnHistoryClick");
    }

    [Fact]
    public void CodeBehind_PreservesEventHandlers()
    {
        var src = LoadCodeBehind();
        Assert.Contains("OnPrimaryButtonClick", src, StringComparison.Ordinal);
        Assert.Contains("OnGeneratorTabSelectionChanged", src, StringComparison.Ordinal);
        Assert.Contains("OnHistoryClick", src, StringComparison.Ordinal);
    }

    [Fact]
    public void TabView_RetainsNamesAndSelectionHandler()
    {
        var doc = Load();
        var tabView = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "TabView");
        Assert.NotNull(tabView);
        Assert.Equal("GeneratorTabs", (string?)tabView!.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")));
        Assert.Equal("OnGeneratorTabSelectionChanged", (string?)tabView.Attribute("SelectionChanged"));

        var passwordTab = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "TabViewItem"
                && (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "PasswordTab");
        Assert.NotNull(passwordTab);
    }
}
