using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public sealed class ResponsiveLayoutXamlTests
{
    [Fact]
    public void VaultHasWideNarrowListAndNarrowDetailStates()
    {
        var text = Read("VaultPage.xaml");
        Assert.Contains("x:Name=\"Wide\"", text);
        Assert.Contains("x:Name=\"NarrowList\"", text);
        Assert.Contains("x:Name=\"NarrowDetail\"", text);
        Assert.Contains("VaultNarrowBackButton", text);
        Assert.Contains("Target=\"PendingCipherCreateActions.Orientation\" Value=\"Horizontal\"", text);
        Assert.Equal(
            2,
            CountOccurrences(
                text,
                "Target=\"PendingCipherCreateActions.Orientation\" Value=\"Vertical\""));
        Assert.Equal(
            2,
            CountOccurrences(
                text,
                "Target=\"PendingCipherCreateCheckButton.HorizontalAlignment\" Value=\"Stretch\""));
        Assert.Equal(
            2,
            CountOccurrences(
                text,
                "Target=\"PendingCipherCreateAbandonButton.HorizontalAlignment\" Value=\"Stretch\""));
        var source = ReadCodeBehind("VaultPage.xaml.cs");
        Assert.Contains("VaultWideThreshold = 800", source);
        Assert.Contains("previousState == \"NarrowList\" && state == \"NarrowDetail\"", source);
        Assert.Contains("VaultNarrowBackButton.Focus(FocusState.Programmatic)", source);
        Assert.Contains("if (!ViewModel.CanCancelEditor)", source);
        Assert.Contains("CipherList.ContainerFromItem(item)", source);
        Assert.Contains("CipherList.Focus(FocusState.Programmatic)", source);
    }

    [Fact]
    public void SendHasAdaptiveColumnsWithoutFixed180Or104()
    {
        var text = Read("SendPage.xaml");
        Assert.Contains("WideColumns", text);
        Assert.Contains("NarrowColumns", text);
        Assert.Contains("Target=\"AccessColumn.Width\" Value=\"0\"", text);
        Assert.Contains("Target=\"CompactAccessText.Visibility\" Value=\"Visible\"", text);
        Assert.DoesNotContain("Width=\"180\"", text);
        Assert.DoesNotContain("Width=\"104\"", text);
    }

    [Theory]
    [InlineData("LoginPage.xaml", "LoginForm")]
    [InlineData("RegisterPage.xaml", "RegisterForm")]
    public void AuthFormsUseMaxWidthAndStretch(string file, string name)
    {
        var document = XDocument.Parse(Read(file));
        var panel = document.Descendants().Single(node =>
            node.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == name));
        Assert.Equal("400", panel.Attribute("MaxWidth")?.Value);
        Assert.Null(panel.Attribute("Width"));
        Assert.Equal("Stretch", panel.Attribute("HorizontalAlignment")?.Value);
    }

    [Theory]
    [InlineData("GeneratorDialog.xaml")]
    [InlineData("GeneratorHistoryDialog.xaml")]
    [InlineData("SendEditorDialog.xaml")]
    public void DialogsHaveNoFixedMinWidthOrWidth(string file)
    {
        var text = Read(file);
        Assert.DoesNotContain("MinWidth=\"620\"", text);
        Assert.DoesNotContain("MinWidth=\"520\"", text);
        Assert.DoesNotContain("<StackPanel Width=\"520\"", text);
    }

    [Theory]
    [InlineData("GeneratorDialog.xaml.cs")]
    [InlineData("GeneratorHistoryDialog.xaml.cs")]
    [InlineData("SendEditorDialog.xaml.cs")]
    public void DialogsApplyResponsiveClamp(string file)
    {
        Assert.Contains("ResponsiveDialogSizing.Clamp", ReadCodeBehind(file));
    }

    private static string Read(string file)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(
                directory.FullName, "src", "App", "Views", file);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException(file);
    }

    private static string ReadCodeBehind(string file)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var viewsPath = Path.Combine(
                directory.FullName, "src", "App", "Views", file);
            if (File.Exists(viewsPath))
                return File.ReadAllText(viewsPath);
        }
        throw new FileNotFoundException(file);
    }

    private static int CountOccurrences(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;
}
