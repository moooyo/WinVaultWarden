using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace App.Tests;

/// <summary>
/// Protects the settings subpage, state-specific actions, AOT-safe bindings,
/// and local handling of takeover credentials.
/// </summary>
public class EmergencyAccessPageXamlTests
{
    private static string Xaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "EmergencyAccessPage.xaml");
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find EmergencyAccessPage.xaml from the test output directory.");
    }

    private static string CodeBehind()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "EmergencyAccessPage.xaml.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find EmergencyAccessPage.xaml.cs from the test output directory.");
    }

    [Fact]
    public void Page_BindsTwoSections()
    {
        var xaml = Xaml();
        Assert.Contains("ViewModel.MyContacts", xaml);
        Assert.Contains("ViewModel.TrustedByOthers", xaml);
        Assert.Contains("Text=\"你信任的人\"", xaml);
        Assert.Contains("Text=\"信任你的人\"", xaml);
    }

    [Fact]
    public void Page_UsesCompactContactRowsAndLeavesBreadcrumbToSettingsHost()
    {
        var document = XDocument.Parse(Xaml());
        var scrollViewer = document.Descendants().Single(e => e.Name.LocalName == "ScrollViewer");
        var container = scrollViewer.Elements().Single();

        Assert.Null(container.Attribute("MaxWidth"));
        Assert.Equal("680", container.Elements().Single().Attribute("MaxWidth")?.Value);
        Assert.Equal("40,8,40,48", container.Attribute("Padding")?.Value);
        Assert.Equal("Disabled", scrollViewer.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.DoesNotContain(document.Descendants(), e => e.Name.LocalName == "BreadcrumbBar"
            || e.Attribute("Text")?.Value == "紧急访问");
        Assert.DoesNotContain(document.Descendants(), e =>
            e.Attribute("Style")?.Value == "{StaticResource CardBorderStyle}");

        var rowStyle = document.Descendants().Single(e =>
            e.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Key")?.Value == "EmergencyRowStyle");
        Assert.Contains(rowStyle.Elements(), e =>
            e.Attribute("Property")?.Value == "MinHeight" && e.Attribute("Value")?.Value == "60");
        Assert.Contains(rowStyle.Elements(), e =>
            e.Attribute("Property")?.Value == "BorderThickness" && e.Attribute("Value")?.Value == "0,0,0,1");
    }

    [Fact]
    public void Page_NoReflectionBinding()
    {
        // Compiled bindings are required for the native AOT build.
        var xaml = Xaml();
        Assert.DoesNotContain("{Binding ", xaml);
    }

    [Fact]
    public void Page_HasFourStateContainers()
    {
        var xaml = Xaml();
        Assert.Contains("ViewModel.ShowLoading", xaml);
        Assert.Contains("ViewModel.ShowError", xaml);
        Assert.Contains("ViewModel.ShowEmpty", xaml);
        Assert.Contains("ViewModel.ShowContent", xaml);
    }

    [Fact]
    public void Page_EachSection_HasOwnEmptyStateBoundToHasNoXxx()
    {
        var xaml = Xaml();
        Assert.Contains("ViewModel.HasNoContacts", xaml);
        Assert.Contains("ViewModel.HasNoGranted", xaml);
    }

    // Each supported row action must keep its own state-dependent visibility.

    [Theory]
    [InlineData("ShowReinvite", "OnReinviteClick")]
    [InlineData("ShowConfirm", "OnConfirmClick")]
    [InlineData("ShowUpdate", "OnUpdateClick")]
    [InlineData("ShowApprove", "OnApproveClick")]
    [InlineData("ShowReject", "OnRejectClick")]
    [InlineData("ShowRemove", "OnRemoveContactClick")]
    [InlineData("ShowInitiate", "OnInitiateClick")]
    [InlineData("ShowView", "OnViewClick")]
    [InlineData("ShowTakeover", "OnTakeoverClick")]
    public void Page_ButtonVisibility_BoundToStateMatrixFunction(string functionName, string clickHandler)
    {
        var document = XDocument.Parse(Xaml());
        var button = document.Descendants().Single(e => e.Name.LocalName == "Button"
            && e.Attribute("Click")?.Value == clickHandler);
        Assert.Contains($"local:EmergencyAccessPage.{functionName}(", button.Attribute("Visibility")?.Value);
        Assert.Equal("{x:Bind}", button.Attribute("Tag")?.Value);
    }

    [Fact]
    public void Page_InviteAndListsAreDisabled_WhenBusy()
    {
        var document = XDocument.Parse(Xaml());
        var invite = document.Descendants().Single(e =>
            e.Attribute("AutomationProperties.AutomationId")?.Value == "EmergencyInviteButton");
        Assert.Equal("OnInviteClick", invite.Attribute("Click")?.Value);
        Assert.Contains("ViewModel.CanOperate", invite.Attribute("IsEnabled")?.Value);
        foreach (var name in new[] { "MyContactsList", "TrustedByOthersList" })
        {
            var list = document.Descendants().Single(e =>
                e.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name")?.Value == name);
            Assert.Contains("ViewModel.CanOperate", list.Attribute("IsEnabled")?.Value);
            Assert.Equal("None", list.Attribute("SelectionMode")?.Value);
        }
    }

    [Fact]
    public void CodeBehind_HasStateMatrixStaticFunctions()
    {
        var cs = CodeBehind();
        Assert.Contains("public static Visibility ShowReinvite(EmergencyAccessStatus status)", cs);
        Assert.Contains("public static Visibility ShowConfirm(EmergencyAccessStatus status)", cs);
        Assert.Contains("public static Visibility ShowUpdate(EmergencyAccessStatus status)", cs);
        Assert.Contains("public static Visibility ShowApprove(EmergencyAccessStatus status)", cs);
        Assert.Contains("public static Visibility ShowReject(EmergencyAccessStatus status)", cs);
        Assert.Contains("public static Visibility ShowRemove(EmergencyAccessStatus status)", cs);
        Assert.Contains("public static Visibility ShowInitiate(EmergencyAccessStatus status)", cs);
        Assert.Contains("public static Visibility ShowView(", cs);
        Assert.Contains("public static Visibility ShowTakeover(", cs);
    }

    [Fact]
    public void CodeBehind_InvokesItemBasedMethods_NotSelectedIdProperties()
    {
        var cs = CodeBehind();
        Assert.Contains("ViewModel.InviteAsync(", cs);
        Assert.Contains("ViewModel.ConfirmAsync(item", cs);
        Assert.Contains("ViewModel.ReinviteAsync(item", cs);
        Assert.Contains("ViewModel.ApproveAsync(item", cs);
        Assert.Contains("ViewModel.RejectAsync(", cs);
        Assert.Contains("ViewModel.RemoveAsync(", cs);
        Assert.Contains("ViewModel.UpdateAsync(item", cs);
        Assert.Contains("ViewModel.InitiateAsync(item", cs);
        Assert.Contains("ViewModel.ViewAsync(item", cs);
        Assert.Contains("ViewModel.TakeoverAsync(item", cs);

        // Actions must use their captured item rather than mutable selection.
        Assert.DoesNotContain("SelectedContactId", cs);
        Assert.DoesNotContain("SelectedGranteeId", cs);
        Assert.DoesNotContain("SelectedGrantedId", cs);
        Assert.DoesNotContain("SelectedGrantorEmail", cs);
    }

    [Fact]
    public void CodeBehind_ViewClick_ConsumesThisInvocationValue_NotStaleViewModelProperty()
    {
        var cs = CodeBehind();
        Assert.Contains("result.Value is { } recovered", cs);
        Assert.DoesNotContain("HasRecoveredVault", cs);
        Assert.DoesNotContain("ViewModel.RecoveredVault", cs);
    }

    [Fact]
    public void RecoveredVaultDialog_UsesSafePresentationItems_InsteadOfRawCiphers()
    {
        var xaml = Xaml();
        var cs = CodeBehind();

        Assert.Contains("x:DataType=\"viewModels:RecoveredCipherListItem\"", xaml);
        Assert.Contains("Text=\"{x:Bind Name}\"", xaml);
        Assert.Contains("Text=\"{x:Bind Subtitle}\"", xaml);
        Assert.Contains("Select(RecoveredCipherListItem.FromCipher)", cs);
        Assert.Contains("ItemTemplate = (DataTemplate)Resources[\"RecoveredCipherItemTemplate\"]", cs);
        Assert.DoesNotContain("ItemsSource = recovered.Ciphers", cs);
    }

    [Fact]
    public void CodeBehind_RemoveAndReject_RequireConfirmationDialog()
    {
        var cs = CodeBehind();
        var reject = Section(cs, "private async void OnRejectClick", "private async void OnRemoveContactClick");
        var remove = Section(cs, "private async void OnRemoveContactClick", "private async void OnUpdateClick");
        var confirmation = Section(cs, "private async Task<bool> ConfirmEmergencyAsync", "public static Visibility ShowReinvite");

        Assert.Contains("ViewModel.RejectAsync(", reject);
        Assert.Contains("DialogHelper.ConfirmAsync(", reject);
        Assert.Contains("ViewModel.RemoveAsync(", remove);
        Assert.Contains("ConfirmEmergencyAsync(", remove);
        Assert.Contains("dangerous: true", remove);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", confirmation);
        Assert.Contains("DialogHelper.ShowTrackedAsync(dialog, ct: ct)", confirmation);
    }

    // Takeover passwords remain local and are cleared on every exit path.

    [Fact]
    public void CodeBehind_TakeoverDoesNotStorePasswordOnViewModel()
    {
        var cs = CodeBehind();
        Assert.DoesNotContain("ViewModel.TakeoverNewPassword", cs);
    }

    [Fact]
    public void CodeBehind_TakeoverClearsPasswordBoxesInFinally()
    {
        var cs = CodeBehind();
        var startIdx = cs.IndexOf("ShowTakeoverDialogAsync", StringComparison.Ordinal);
        Assert.True(startIdx >= 0, "ShowTakeoverDialogAsync not found");
        var finallyIdx = cs.IndexOf("finally", startIdx, StringComparison.Ordinal);
        Assert.True(finallyIdx > startIdx, "finally block not found after ShowTakeoverDialogAsync");
        var tail = cs.Substring(finallyIdx, Math.Min(300, cs.Length - finallyIdx));
        Assert.Contains("pwBox.Password = string.Empty", tail);
        Assert.Contains("confirmBox.Password = string.Empty", tail);
    }

    [Fact]
    public void ViewModel_NoLongerExposesTakeoverPasswordOrSelectedIds()
    {
        var vmPath = FindFromOutput("src", "App", "ViewModels", "EmergencyAccessViewModel.cs");
        var cs = File.ReadAllText(vmPath);
        Assert.DoesNotContain("TakeoverNewPassword", cs);
        Assert.DoesNotContain("SelectedContactId", cs);
        Assert.DoesNotContain("SelectedGranteeId", cs);
        Assert.DoesNotContain("SelectedGrantedId", cs);
        Assert.DoesNotContain("SelectedGrantorEmail", cs);
        Assert.DoesNotContain("HasRecoveredVault", cs);
    }

    private static string FindFromOutput(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(path))
                return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find " + Path.Combine(segments));
    }

    private static string Section(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source section: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source section end: {endMarker}");
        return source[start..end];
    }
}
