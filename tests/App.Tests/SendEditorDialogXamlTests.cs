using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class SendEditorDialogXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void SendEditorDialog_ScrollsContentWithinAnchoredNativeFooter()
    {
        var document = LoadDialogXaml();
        var root = document.Root!;
        var scroller = root.Elements(Xaml + "ScrollViewer").Single();
        var form = scroller.Elements(Xaml + "StackPanel").Single();

        Assert.Null(root.Attribute("MinWidth"));
        Assert.Null(root.Attribute("MaxWidth"));
        Assert.Null(root.Attribute("HorizontalAlignment"));
        Assert.Null(root.Attribute("VerticalAlignment"));
        Assert.Equal("Disabled", scroller.Attribute("HorizontalScrollMode")?.Value);
        Assert.Contains("SendEditorScrollViewer.MaxHeight", CodeBehindText());
        Assert.DoesNotContain(
            document.Descendants(Xaml + "Border"),
            element => element.Attribute("MinWidth") is not null || element.Attribute("MaxWidth") is not null);
        Assert.Equal("640", ResourceValue(document, "ContentDialogMaxWidth"));
        Assert.Equal("960", ResourceValue(document, "ContentDialogMaxHeight"));
        // Task 13: 固定 Width="520" 已移除,改为按 XamlRoot 可用宽度在 code-behind clamp。
        Assert.Null(form.Attribute("Width"));
        Assert.Equal("SendEditorContent", form.Attribute(X + "Name")?.Value);
        Assert.Null(form.Attribute("Padding"));
    }

    [Fact]
    public void SendEditorDialog_PrimaryInputsStretchWithinForm()
    {
        var document = LoadDialogXaml();

        var inputNames = document
            .Descendants()
            .Where(element => element.Name == Xaml + "TextBox"
                || element.Name == Xaml + "ComboBox"
                || element.Name == Xaml + "NumberBox"
                || element.Name == Xaml + "PasswordBox")
            .Select(element => element.Attribute("HorizontalAlignment")?.Value)
            .ToArray();

        Assert.NotEmpty(inputNames);
        Assert.All(inputNames, alignment => Assert.Equal("Stretch", alignment));
    }

    [Fact]
    public void Dialog_HasPasswordBox_BoundToDraftPassword()
    {
        var doc = LoadDialogXaml();
        var pw = doc.Descendants(Xaml + "PasswordBox").Single();
        Assert.Contains("Draft.Password", pw.Attribute("Password")?.Value);
    }

    [Fact]
    public void Dialog_HasDisabledToggle_BoundToDraftDisabled()
    {
        var doc = LoadDialogXaml();
        var toggle = doc.Descendants(Xaml + "ToggleSwitch").Single();
        Assert.Contains("Draft.Disabled", toggle.Attribute("IsOn")?.Value);
    }

    [Fact]
    public void Dialog_DeletionCombo_IncludesCustomOption()
    {
        var doc = LoadDialogXaml();
        var strings = doc.Descendants(X + "String").Select(e => e.Value).ToList();
        Assert.Contains("自定义", strings);
    }

    [Fact]
    public void Dialog_HasCalendarDatePicker_BoundToDraftDeletionDate()
    {
        var doc = LoadDialogXaml();
        var picker = doc
            .Descendants(Xaml + "CalendarDatePicker")
            .SingleOrDefault(element =>
                (element.Attribute("Date")?.Value ?? string.Empty).Contains("Draft.DeletionDate"));
        Assert.NotNull(picker);
        Assert.Contains("Draft.DeletionDate", picker!.Attribute("Date")?.Value ?? string.Empty);
        Assert.Contains("ShowCustomDeletionPicker", picker.Attribute("Visibility")?.Value ?? string.Empty);
    }

    [Fact]
    public void Dialog_CustomExpirationUsesOneWayInitializationAndExplicitChangeHandlers()
    {
        var doc = LoadDialogXaml();
        var expirationCombo = doc
            .Descendants(Xaml + "ComboBox")
            .Single(element => element.Attribute("Header")?.Value == "停止访问时间（可选）");
        var customPicker = doc
            .Descendants(Xaml + "CalendarDatePicker")
            .Single(element => element.Attribute("Header")?.Value == "自定义过期日期");

        Assert.Equal(
            "{x:Bind Draft.ExpirationDateLabel, Mode=OneWay}",
            expirationCombo.Attribute("SelectedItem")?.Value);
        Assert.Equal("OnExpirationLabelChanged", expirationCombo.Attribute("SelectionChanged")?.Value);
        Assert.Contains(
            expirationCombo.Elements(X + "String"),
            element => element.Value == "自定义");
        Assert.Equal(
            "{x:Bind Draft.CustomExpirationDate, Mode=OneWay}",
            customPicker.Attribute("Date")?.Value);
        Assert.Equal("OnCustomExpirationDateChanged", customPicker.Attribute("DateChanged")?.Value);
        var code = CodeBehindText();
        var initialBindingUpdate = code.IndexOf("Bindings?.Update();", StringComparison.Ordinal);
        var expirationChangesEnabled = code.IndexOf(
            "_canApplyExpirationChanges = true;",
            StringComparison.Ordinal);
        Assert.True(initialBindingUpdate >= 0);
        Assert.True(expirationChangesEnabled > initialBindingUpdate);
        Assert.Contains("if (!_canApplyExpirationChanges", code);
    }

    [Fact]
    public void DialogHasInlineRetryableErrorAndPrimaryDeferralHandler()
    {
        // 保存在 Primary deferral 内完成:失败时 args.Cancel=true 保留对话框、经内联 InfoBar 展示可重试错误,
        // 且区分 Failed 与 Busy/Cancelled。
        var xaml = XamlText();
        var code = CodeBehindText();
        Assert.Contains("SendEditorErrorInfoBar", xaml);
        Assert.Contains("args.GetDeferral()", code);
        Assert.Contains("args.Cancel = true", code);
        Assert.Contains("UiOperationStatus.Failed", code);
    }

    [Fact]
    public void RadioButtons_BindCheckedStateToDraftType()
    {
        // 单选按钮的 IsChecked 绑定真实草稿类型(OneWay),编辑既有文件 Send 时初始化不会把 File 误显为 Text。
        var document = LoadDialogXaml();
        var radios = document.Descendants(Xaml + "RadioButton").ToList();
        Assert.Equal(2, radios.Count);
        var checkedBindings = radios
            .Select(r => r.Attribute("IsChecked")?.Value ?? string.Empty)
            .ToList();
        Assert.Contains(checkedBindings, v => v.Contains("Draft.IsText"));
        Assert.Contains(checkedBindings, v => v.Contains("Draft.IsFile"));
        // 不得再有硬编码 IsChecked="True" 覆盖真实类型。
        Assert.DoesNotContain(checkedBindings, v => v == "True");
    }

    private static XDocument LoadDialogXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "SendEditorDialog.xaml");
            if (File.Exists(path))
                return XDocument.Load(path);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find SendEditorDialog.xaml from the test output directory.");
    }

    private static string XamlText() => ReadSource("SendEditorDialog.xaml");
    private static string CodeBehindText() => ReadSource("SendEditorDialog.xaml.cs");

    private static string ReadSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", fileName);
            if (File.Exists(path))
                return File.ReadAllText(path);

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {fileName} from the test output directory.");
    }

    private static string? ResourceValue(XDocument document, string key) =>
        document
            .Descendants()
            .FirstOrDefault(element => element.Attribute(X + "Key")?.Value == key)
            ?.Value;
}
