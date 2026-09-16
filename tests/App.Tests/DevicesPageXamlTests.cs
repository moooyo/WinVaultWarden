using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class DevicesPageXamlTests
{
    private static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "DevicesPage.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("DevicesPage.xaml not found.");
    }

    [Fact]
    public void DeviceCard_UsesSharedThemeCardStyle()
    {
        var doc = Load();
        var repeater = doc.Descendants().First(e => e.Name.LocalName == "ItemsRepeater"
            && e.Attribute("ItemsSource")?.Value == "{x:Bind ViewModel.Devices}");
        var card = repeater.Descendants().FirstOrDefault(e => e.Name.LocalName == "Border"
            && e.Attribute("Style")?.Value == "{StaticResource CardBorderStyle}");
        Assert.NotNull(card);

        Assert.DoesNotContain(repeater.Descendants(), e => e.Name.LocalName == "Border"
            && e.Attribute("Background")?.Value == "{ThemeResource CardBackgroundFillColorDefaultBrush}");
    }

    [Fact]
    public void DeviceCard_ShowsRegistrationMetadata()
    {
        var doc = Load();
        var card = doc.Descendants().First(e => e.Name.LocalName == "TextBlock"
            && e.Attribute("Text")?.Value == "{x:Bind RegisteredLabel}");
        Assert.NotNull(card);
    }

    [Fact]
    public void DeviceCard_SeparatesCurrentAndTrustedIndicators()
    {
        var doc = Load();
        Assert.Contains(doc.Descendants(), e => e.Attribute("Visibility")?.Value.Contains("IsCurrent") == true);
        Assert.Contains(doc.Descendants(), e => e.Attribute("Visibility")?.Value.Contains("IsTrusted") == true);
    }

    [Fact]
    public void NoElevenPxText_Remains()
    {
        var doc = Load();
        Assert.DoesNotContain(doc.Descendants(), e => e.Attribute("FontSize")?.Value == "11");
    }


    [Fact]
    public void PendingSection_HasRefreshButton_WithAutomationId()
    {
        var doc = Load();
        var button = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Button"
                && e.Attribute("AutomationProperties.AutomationId")?.Value == "DevicesRefreshRequestsButton");
        Assert.NotNull(button);
    }

    [Fact]
    public void PendingSection_HasApproveButton_WithAutomationId()
    {
        var doc = Load();
        var button = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Button"
                && e.Attribute("AutomationProperties.AutomationId")?.Value == "DevicesApproveRequestButton");
        Assert.NotNull(button);
    }

    [Fact]
    public void PendingSection_HasDenyButton_WithAutomationId()
    {
        var doc = Load();
        var button = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Button"
                && e.Attribute("AutomationProperties.AutomationId")?.Value == "DevicesDenyRequestButton");
        Assert.NotNull(button);
    }

    [Fact]
    public void PendingSection_HasItemsRepeater_BoundToPendingRequests()
    {
        var doc = Load();
        var repeater = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ItemsRepeater"
                && (e.Attribute("ItemsSource")?.Value.Contains("PendingRequests") ?? false));
        Assert.NotNull(repeater);
    }

    [Fact]
    public void PendingSection_RepeaterIsDisabledByCanOperate()
    {
        var doc = Load();
        var repeater = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ContentControl"
                && e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "PendingRequestsRepeater");
        Assert.NotNull(repeater);
        Assert.Contains("ViewModel.CanOperate", repeater!.Attribute("IsEnabled")?.Value);
    }

    [Fact]
    public void PendingSection_DataTemplate_HasAuthRequestItemDataType()
    {
        var doc = Load();
        // DataTemplate x:DataType="models:AuthRequestItem"
        var template = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "DataTemplate"
                && (e.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}DataType")?.Value.Contains("AuthRequestItem") ?? false));
        Assert.NotNull(template);
    }

    [Fact]
    public void PendingSection_ShowsDeviceTypeName()
    {
        var doc = Load();
        var card = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "TextBlock"
                && e.Attribute("Text")?.Value == "{x:Bind DeviceTypeName}");
        Assert.NotNull(card);
    }

    [Fact]
    public void PendingSection_ShowsIpAddress()
    {
        var doc = Load();
        var textBlock = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Run"
                && e.Attribute("Text")?.Value == "{x:Bind IpAddress}");
        Assert.NotNull(textBlock);
    }

    [Fact]
    public void PendingSection_ShowsCreatedLabel()
    {
        var doc = Load();
        var textBlock = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "TextBlock"
                && e.Attribute("Text")?.Value == "{x:Bind CreatedLabel}");
        Assert.NotNull(textBlock);
    }

    [Fact]
    public void PendingSection_ShowsFingerprintPhrase()
    {
        var doc = Load();
        var textBlock = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "TextBlock"
                && e.Attribute("Text")?.Value == "{x:Bind FingerprintPhrase}");
        Assert.NotNull(textBlock);
    }

    [Fact]
    public void PendingSection_EmptyStateTextBlock_BoundToShowRequestsEmpty()
    {
        var doc = Load();
        var textBlock = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Border"
                && (e.Attribute("Visibility")?.Value.Contains("ShowRequestsEmpty") ?? false));
        Assert.NotNull(textBlock);
    }

    [Fact]
    public void NoRevokeButton_Present()
    {
        var doc = Load();
        var revokeButton = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Button"
                && (e.Attribute("Content")?.Value == "撤销"
                    || e.Attribute("AutomationProperties.AutomationId")?.Value?.Contains("Revoke") == true
                    || e.Attribute("Click")?.Value?.Contains("Revoke") == true));
        Assert.Null(revokeButton);
    }
}
