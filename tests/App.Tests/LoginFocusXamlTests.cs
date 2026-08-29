using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public sealed class LoginFocusXamlTests
{
    [Theory]
    [InlineData("LoginPage.xaml", "OnSubmitAccelerator")]
    [InlineData("RegisterPage.xaml", "OnSubmitAccelerator")]
    public void PageHasEnterAccelerator(string file, string handler)
    {
        var document = Load(file);
        var accelerator = document.Descendants()
            .Single(node => node.Name.LocalName == "KeyboardAccelerator"
                && node.Attribute("Key")?.Value == "Enter");
        Assert.Equal(handler, accelerator.Attribute("Invoked")?.Value);
    }

    [Fact]
    public void RegisterWholeFormIsDisabledWhileBusy()
    {
        var document = Load("RegisterPage.xaml");
        var panel = document.Descendants().Single(node =>
            node.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "RegisterInputPanel"));
        Assert.Contains("ViewModel.CanEditForm", panel.Attribute("IsEnabled")?.Value);
    }

    [Fact]
    public void LoginBackButton_HasAccessibleName()
    {
        var document = Load("LoginPage.xaml");
        var button = document.Descendants().Single(node =>
            node.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "BackButton"));

        Assert.Equal("返回", button.Attribute("AutomationProperties.Name")?.Value);
    }

    private static XDocument Load(string file)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(
                directory.FullName, "src", "App", "Views", file);
            if (File.Exists(path))
                return XDocument.Load(path);
        }
        throw new FileNotFoundException(file);
    }
}
