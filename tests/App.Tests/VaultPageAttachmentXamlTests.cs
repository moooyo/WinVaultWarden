using System.Xml.Linq;
using Xunit;

namespace App.Tests;

// Task 5: 附件区 UX —— 校验/进度/禁用/空状态/计数/图标 的 XAML 串断言。
public class VaultPageAttachmentXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AttachmentHeader_BoundToDetailAttachmentHeader()
    {
        var xaml = LoadXamlText();
        Assert.Contains("Text=\"{x:Bind ViewModel.Detail.AttachmentHeader, Mode=OneWay}\"", xaml);
    }

    [Fact]
    public void EmptyState_ShowsWhenNoAttachments_ViaInverseBoolToVis()
    {
        var xaml = LoadXamlText();
        Assert.Contains("暂无附件", xaml);
        Assert.Contains(
            "Visibility=\"{x:Bind ViewModel.Detail.HasAttachments, Mode=OneWay, Converter={StaticResource InverseBoolToVis}}\"",
            xaml);
    }

    [Fact]
    public void AttachmentsItemsControl_VisibleWhenHasAttachments_ViaBoolToVis()
    {
        var document = LoadXaml();
        var itemsControl = document.Descendants(Xaml + "ItemsControl")
            .Single(e => e.Attribute(X + "Name")?.Value == "AttachmentsItems");

        Assert.Equal(
            "{x:Bind ViewModel.Detail.HasAttachments, Mode=OneWay, Converter={StaticResource BoolToVis}}",
            itemsControl.Attribute("Visibility")?.Value);
    }

    [Fact]
    public void ProgressRow_BoundToIsAttachmentBusyAndBusyText_WithEmptyToCollapsed()
    {
        var xaml = LoadXamlText();
        Assert.Contains("IsActive=\"{x:Bind ViewModel.IsAttachmentBusy, Mode=OneWay}\"", xaml);
        Assert.Contains("Text=\"{x:Bind ViewModel.AttachmentBusyText, Mode=OneWay}\"", xaml);
        Assert.Contains(
            "Visibility=\"{x:Bind ViewModel.AttachmentBusyText, Mode=OneWay, Converter={StaticResource EmptyToCollapsed}}\"",
            xaml);
    }

    [Fact]
    public void DownloadDeleteAddButtons_DisabledWhileAttachmentBusy()
    {
        var document = LoadXaml();

        // 下载/删除按钮位于 DataTemplate(x:DataType=AttachmentItem)内,x:Bind 无法从模板内部
        // 反向引用页面级 ViewModel(WinUI x:Bind 编译绑定作用域限制);因此禁用状态改在外层
        // AttachmentsItems ItemsControl 上绑定 IsEnabled —— Control.IsEnabled=false 会向下
        // 级联到所有子控件(含模板内生成的按钮),等效于逐按钮禁用。
        var itemsControl = document.Descendants(Xaml + "ItemsControl")
            .Single(e => e.Attribute(X + "Name")?.Value == "AttachmentsItems");
        var addButton = document.Descendants(Xaml + "Button")
            .Single(b => b.Attribute(X + "Name")?.Value == "AddAttachmentButton");

        const string expected = "{x:Bind ViewModel.IsNotAttachmentBusy, Mode=OneWay}";
        Assert.Equal(expected, itemsControl.Attribute("IsEnabled")?.Value);
        Assert.Equal(expected, addButton.Attribute("IsEnabled")?.Value);

        // 下载/删除按钮本身仍保留 Click 处理程序,不再单独携带 IsEnabled(由父级级联控制)。
        var downloadButton = document.Descendants(Xaml + "Button")
            .Single(b => b.Attribute("Click")?.Value == "OnDownloadAttachmentClick");
        var deleteButton = document.Descendants(Xaml + "Button")
            .Single(b => b.Attribute("Click")?.Value == "OnDeleteAttachmentClick");
        Assert.NotNull(downloadButton);
        Assert.NotNull(deleteButton);
    }

    [Fact]
    public void AttachmentRow_TypeIcon_BoundToGlyph()
    {
        var document = LoadXaml();
        var itemsControl = document.Descendants(Xaml + "ItemsControl")
            .Single(e => e.Attribute(X + "Name")?.Value == "AttachmentsItems");

        var typeIcon = itemsControl.Descendants(Xaml + "FontIcon").First();
        Assert.Equal("{x:Bind Glyph}", typeIcon.Attribute("Glyph")?.Value);
    }

    private static string LoadXamlText() => File.ReadAllText(FindXamlPath());

    private static XDocument LoadXaml() => XDocument.Load(FindXamlPath());

    private static string FindXamlPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path))
                return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find VaultPage.xaml from the test output directory.");
    }
}
