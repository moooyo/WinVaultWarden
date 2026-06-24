using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class CipherEditorXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void VaultPage_AddMenu_ContainsCipherTypesAndFolder()
    {
        var document = LoadVaultPageXaml();
        var addButton = RequireByName(document, "AddCipherButton");
        var flyoutItems = addButton.Descendants(Xaml + "MenuFlyoutItem")
            .Select(element => element.Attribute("Text")?.Value)
            .Where(text => text is not null)
            .ToArray();

        Assert.Contains("登录", flyoutItems);
        Assert.Contains("支付卡", flyoutItems);
        Assert.Contains("身份", flyoutItems);
        Assert.Contains("笔记", flyoutItems);
        Assert.Contains("SSH 密钥", flyoutItems);
        Assert.Contains("文件夹", flyoutItems);
    }

    [Fact]
    public void VaultPage_EditorActions_HaveStableNames()
    {
        var document = LoadVaultPageXaml();

        RequireByName(document, "SaveCipherEditorButton");
        RequireByName(document, "CancelCipherEditorButton");
        RequireByName(document, "CipherEditorPanel");
        RequireByName(document, "CipherEditorTypeBox");
    }

    [Fact]
    public void VaultPage_EditorInputs_StretchHorizontally()
    {
        var document = LoadVaultPageXaml();
        var editorPanel = RequireByName(document, "CipherEditorPanel");

        AssertStretchInputs(editorPanel, "TextBox");
        AssertStretchInputs(editorPanel, "PasswordBox");
        AssertStretchInputs(editorPanel, "ComboBox");
    }

    [Fact]
    public void VaultPage_EditorPanel_DoesNotNestScrollViewer()
    {
        var document = LoadVaultPageXaml();
        var editorPanel = RequireByName(document, "CipherEditorPanel");

        Assert.NotEqual(Xaml + "ScrollViewer", editorPanel.Name);
        Assert.Empty(editorPanel.Descendants(Xaml + "ScrollViewer"));
    }

    private static XElement? FindByName(XDocument document, string name) =>
        document.Descendants().FirstOrDefault(element => element.Attribute(X + "Name")?.Value == name);

    private static XElement RequireByName(XDocument document, string name)
    {
        var element = FindByName(document, name);
        if (element is null)
        {
            Assert.Fail($"Expected x:Name='{name}' in VaultPage.xaml.");
            throw new InvalidOperationException($"Expected x:Name='{name}' in VaultPage.xaml.");
        }

        return element;
    }

    private static void AssertStretchInputs(XElement editorPanel, string controlName)
    {
        var inputs = editorPanel.Descendants(Xaml + controlName).ToArray();

        Assert.NotEmpty(inputs);
        Assert.All(inputs, input => Assert.Equal("Stretch", input.Attribute("HorizontalAlignment")?.Value));
    }

    private static XDocument LoadVaultPageXaml()
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
