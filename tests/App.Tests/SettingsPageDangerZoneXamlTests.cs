using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class SettingsPageDangerZoneXamlTests
{
    private static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "SettingsPage.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("SettingsPage.xaml not found.");
    }

    [Fact]
    public void DeleteAccountButton_IsEnabledAndWired()
    {
        var doc = Load();
        var deleteButton = doc.Descendants().SingleOrDefault(element =>
            element.Name.LocalName == "Button"
            && (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "DeleteAccountButton");

        Assert.NotNull(deleteButton);
        Assert.NotEqual("False", (string?)deleteButton!.Attribute("IsEnabled"));
        Assert.Equal("DeleteAccountButton", (string?)deleteButton.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")));
        Assert.Equal("删除账户…", (string?)deleteButton.Attribute("Content"));
        Assert.Equal("OnDeleteAccountClick", (string?)deleteButton.Attribute("Click"));
        Assert.Equal(
            "DeleteAccountButton",
            deleteButton.Attributes().First(attribute =>
                attribute.Name.LocalName == "AutomationProperties.AutomationId").Value);
    }

    [Fact]
    public void DeleteAccountCard_NoLongerAdvertisesPlaceholder()
    {
        var card = Load().Descendants().First(element =>
            element.Name.LocalName == "SettingsCard"
            && (string?)element.Attribute("Header") == "删除账户");

        Assert.DoesNotContain("即将推出", (string?)card.Attribute("Description"), StringComparison.Ordinal);
    }
}
