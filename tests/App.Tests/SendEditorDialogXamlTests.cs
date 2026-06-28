using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class SendEditorDialogXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void SendEditorDialog_ExpandsContentWithoutCustomScrollBar()
    {
        var document = LoadDialogXaml();
        var root = document.Root!;
        var form = root.Elements(Xaml + "StackPanel").Single();

        Assert.Null(root.Attribute("MinWidth"));
        Assert.Null(root.Attribute("MaxWidth"));
        Assert.Null(root.Attribute("HorizontalAlignment"));
        Assert.Null(root.Attribute("VerticalAlignment"));
        Assert.Empty(document.Descendants(Xaml + "ScrollViewer"));
        Assert.DoesNotContain(
            document.Descendants(Xaml + "Border"),
            element => element.Attribute("MinWidth") is not null || element.Attribute("MaxWidth") is not null);
        Assert.Equal("640", ResourceValue(document, "ContentDialogMaxWidth"));
        Assert.Equal("960", ResourceValue(document, "ContentDialogMaxHeight"));
        Assert.Equal("520", form.Attribute("Width")?.Value);
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

    private static string? ResourceValue(XDocument document, string key) =>
        document
            .Descendants()
            .FirstOrDefault(element => element.Attribute(X + "Key")?.Value == key)
            ?.Value;
}
