using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class DevicesPageXamlTests
{
    private static string FindAppFile(string folder, string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", folder, file);
            if (File.Exists(path)) return path;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"{file} not found.");
    }

    private static XDocument Load(string file = "DevicesPage.xaml") =>
        XDocument.Load(FindAppFile("Views", file));

    private static XElement Template(XDocument document, string dataType) =>
        document.Descendants().Single(element => element.Name.LocalName == "DataTemplate"
            && element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}DataType")?.Value == dataType);

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(element =>
            element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name")?.Value == name);

    [Theory]
    [InlineData("models:DeviceItem")]
    [InlineData("models:AuthRequestItem")]
    public void DeviceLists_UseCompactDividerRowsAndNeutralIconTiles(string dataType)
    {
        var doc = Load();
        var template = Template(doc, dataType);
        var row = Assert.Single(template.Elements());
        Assert.Equal("Border", row.Name.LocalName);
        Assert.Equal("{StaticResource DeviceListRowStyle}", row.Attribute("Style")?.Value);

        var rowStyle = doc.Descendants().Single(element => element.Name.LocalName == "Style"
            && element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Key")?.Value == "DeviceListRowStyle");
        Assert.Contains(rowStyle.Elements(), element => element.Attribute("Property")?.Value == "MinHeight"
            && element.Attribute("Value")?.Value == "60");
        Assert.Contains(rowStyle.Elements(), element => element.Attribute("Property")?.Value == "BorderThickness"
            && element.Attribute("Value")?.Value == "0,0,0,1");
        Assert.Contains(rowStyle.Elements(), element => element.Attribute("Property")?.Value == "BorderBrush"
            && element.Attribute("Value")?.Value == "{ThemeResource DividerStrokeColorDefaultBrush}");

        var grid = Assert.Single(row.Elements());
        Assert.Equal("14", grid.Attribute("ColumnSpacing")?.Value);
        var columns = grid.Elements().Single(element => element.Name.LocalName == "Grid.ColumnDefinitions");
        Assert.Equal(new[] { "32", "*", "Auto" }, columns.Elements().Select(element => element.Attribute("Width")?.Value));
        Assert.Contains(grid.Elements(), element => element.Name.LocalName == "Border"
            && element.Attribute("Style")?.Value == "{StaticResource DeviceTileStyle}");

        var tileStyle = doc.Descendants().Single(element => element.Name.LocalName == "Style"
            && element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Key")?.Value == "DeviceTileStyle");
        Assert.Contains(tileStyle.Elements(), element => element.Attribute("Property")?.Value == "Width"
            && element.Attribute("Value")?.Value == "32");
        Assert.Contains(tileStyle.Elements(), element => element.Attribute("Property")?.Value == "Height"
            && element.Attribute("Value")?.Value == "32");
        Assert.Contains(tileStyle.Elements(), element => element.Attribute("Property")?.Value == "Background"
            && element.Attribute("Value")?.Value == "{ThemeResource ControlFillColorSecondaryBrush}");

        Assert.DoesNotContain(template.Descendants(), element =>
            element.Attribute("Style")?.Value == "{StaticResource CardBorderStyle}");
    }

    [Fact]
    public void DeviceRow_ShowsRegistrationMetadataWithoutCallingItLastActivity()
    {
        var template = Template(Load(), "models:DeviceItem");
        Assert.Contains(template.Descendants(), element => element.Name.LocalName == "TextBlock"
            && element.Attribute("Text")?.Value == "{x:Bind RegisteredLabel}");
        Assert.DoesNotContain(template.Descendants().Attributes(), attribute =>
            attribute.Value.Contains("LastActive", StringComparison.Ordinal));
    }

    [Fact]
    public void DeviceRow_SeparatesCurrentAndTrustedInlineIndicators()
    {
        var template = Template(Load(), "models:DeviceItem");
        var current = template.Descendants().Single(element => element.Name.LocalName == "TextBlock"
            && element.Attribute("Visibility")?.Value.Contains("IsCurrent") == true);
        var trusted = template.Descendants().Single(element => element.Name.LocalName == "TextBlock"
            && element.Attribute("Visibility")?.Value.Contains("IsTrusted") == true);
        Assert.Same(current.Parent, trusted.Parent);
        Assert.Equal("Horizontal", current.Parent?.Attribute("Orientation")?.Value);
        Assert.Equal("{StaticResource DeviceSecondaryTextStyle}", current.Attribute("Style")?.Value);
        Assert.Equal("{StaticResource DeviceSecondaryTextStyle}", trusted.Attribute("Style")?.Value);
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
        Assert.Contains("ViewModel.CanOperate", button.Attribute("IsEnabled")?.Value);
    }

    [Fact]
    public void PendingSection_ReviewActionOpensIndependentWindowWithoutApproving()
    {
        var doc = Load();
        var button = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Button"
                && e.Attribute("AutomationProperties.AutomationId")?.Value == "DevicesApproveRequestButton");
        Assert.NotNull(button);
        Assert.Equal("查看", button.Attribute("Content")?.Value);
        Assert.Equal("OnApproveRequestClick", button.Attribute("Click")?.Value);

        var source = File.ReadAllText(FindAppFile("Views", "DevicesPage.xaml.cs"));
        Assert.Contains("_requestWindows.OpenRequest(item)", source);
        Assert.DoesNotContain("ViewModel.ApproveAsync(", source);
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

    [Theory]
    [InlineData("DeviceTypeName")]
    [InlineData("IpAddress")]
    [InlineData("CreatedLabel")]
    public void PendingSection_RendersRequestMetadataInItsRow(string property)
    {
        var template = Template(Load(), "models:AuthRequestItem");
        Assert.Contains(template.Descendants(), element =>
            element.Name.LocalName is "TextBlock" or "Run"
            && element.Attribute("Text")?.Value == $"{{x:Bind {property}}}");
    }

    [Fact]
    public void DeviceApprovalWindow_ShowsTheReviewedFingerprintInsteadOfRepeatingItInTheList()
    {
        var template = Template(Load(), "models:AuthRequestItem");
        Assert.DoesNotContain(template.Descendants().Attributes(), attribute =>
            attribute.Value.Contains("FingerprintPhrase", StringComparison.Ordinal));

        var window = Load("DeviceApprovalWindow.xaml");
        var fingerprint = Named(window, "Fingerprint");
        Assert.Equal("Window", window.Root?.Name.LocalName);
        Assert.Equal("TextBlock", fingerprint.Name.LocalName);
        Assert.Equal("DeviceApprovalFingerprint", fingerprint.Attribute("AutomationProperties.AutomationId")?.Value);
        Assert.Equal("True", fingerprint.Attribute("IsTextSelectionEnabled")?.Value);
        Assert.Equal("Wrap", fingerprint.Attribute("TextWrapping")?.Value);

        var source = File.ReadAllText(FindAppFile("Views", "DeviceApprovalWindow.xaml.cs"));
        Assert.Contains("Fingerprint.Text = string.IsNullOrWhiteSpace(request.FingerprintPhrase)", source);
        Assert.Contains(": request.FingerprintPhrase;", source);
    }

    [Fact]
    public void DeviceApprovalWindow_RequiresExplicitFingerprintReviewAndKeepsBusyGates()
    {
        var window = Load("DeviceApprovalWindow.xaml");
        var reviewed = Named(window, "FingerprintReviewed");
        var approve = Named(window, "ApproveButton");
        Assert.Equal("CheckBox", reviewed.Name.LocalName);
        Assert.True(reviewed.Attribute("IsChecked")?.Value is null or "False");
        Assert.Equal("OnFingerprintReviewed", reviewed.Attribute("Checked")?.Value);
        Assert.Equal("OnFingerprintReviewed", reviewed.Attribute("Unchecked")?.Value);
        Assert.Equal("False", approve.Attribute("IsEnabled")?.Value);
        Assert.Equal("OnApproveClick", approve.Attribute("Click")?.Value);

        var source = File.ReadAllText(FindAppFile("Views", "DeviceApprovalWindow.xaml.cs"));
        Assert.Contains("ApproveButton.IsEnabled = !_busy && FingerprintReviewed.IsChecked == true", source);
        Assert.Contains("&& !string.IsNullOrWhiteSpace(request.FingerprintPhrase)", source);
        Assert.Contains("DenyButton.IsEnabled = !_busy", source);
        Assert.Contains("FingerprintReviewed.IsEnabled = !_busy", source);
        Assert.Contains("_busy || _closed || _request is not { } request || FingerprintReviewed.IsChecked != true", source);
        Assert.Contains("_approve(request, true, _cancellation.Token)", source);

        var service = File.ReadAllText(FindAppFile("Services", "DeviceApprovalWindowService.cs"));
        Assert.Contains("new DeviceApprovalWindow(item, session, lease, ApproveAsync, DenyAsync)", service);
        Assert.Contains("_decisions.ApproveAsync(item, reviewed", service);
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
    public void DeviceRows_DoNotExposeUnsupportedRemoveOrRevokeActions()
    {
        var doc = Load();
        Assert.DoesNotContain(Template(doc, "models:DeviceItem").Descendants(), element =>
            element.Name.LocalName == "Button");
        var revokeButton = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Button"
                && (e.Attribute("Content")?.Value is "撤销" or "移除"
                    || e.Attribute("AutomationProperties.AutomationId")?.Value?.Contains("Revoke") == true
                    || e.Attribute("AutomationProperties.AutomationId")?.Value?.Contains("Remove") == true
                    || e.Attribute("Click")?.Value?.Contains("Revoke") == true
                    || e.Attribute("Click")?.Value?.Contains("Remove") == true));
        Assert.Null(revokeButton);
    }
}
