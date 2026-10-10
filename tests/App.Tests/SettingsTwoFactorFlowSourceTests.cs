using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public sealed class SettingsTwoFactorFlowSourceTests
{
    [Fact]
    public void DynamicRows_ResolveThePageThemeAndRepaintAfterThemeChanges()
    {
        var source = Read("SettingsTwoFactorPage.xaml.cs");
        Assert.Contains("ActualThemeChanged += OnActualThemeChanged", source, StringComparison.Ordinal);
        Assert.Contains("ThemeBrushResolver.Resolve(this", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Application.Current.Resources[", source, StringComparison.Ordinal);
        var start = source.IndexOf("private void OnActualThemeChanged", StringComparison.Ordinal);
        var end = source.IndexOf("public void Initialize", start, StringComparison.Ordinal);
        Assert.Contains("BuildRows()", source[start..end], StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFactorFlow_HasLoadingErrorRetryAndContentStates()
    {
        var document = XDocument.Parse(Read("SettingsTwoFactorPage.xaml"));
        var names = document.Descendants().Select(element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))).ToList();
        var source = Read("SettingsTwoFactorPage.xaml.cs");

        Assert.Contains("LoadingRing", names);
        Assert.Contains("LoadErrorBar", names);
        Assert.Contains("MethodsContent", names);
        Assert.Contains(document.Descendants(), element => element.Name.LocalName == "Button"
            && (string?)element.Attribute("Click") == "OnRetryClick");
        Assert.Contains("LoadingRing.IsActive = true", source, StringComparison.Ordinal);
        Assert.Contains("MethodsContent.Visibility = Visibility.Collapsed", source, StringComparison.Ordinal);
        Assert.Contains("LoadErrorBar.IsOpen = true", source, StringComparison.Ordinal);
        Assert.Contains("MethodsContent.Visibility = Visibility.Visible", source, StringComparison.Ordinal);
        Assert.Contains("OnRetryClick(object sender, RoutedEventArgs e) => await LoadProvidersAsync()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderStatus_IsBuiltOnlyFromSuccessfulValue()
    {
        var source = Read("SettingsTwoFactorPage.xaml.cs");
        var success = source.IndexOf("if (!result.IsSuccess || result.Value is null)", StringComparison.Ordinal);
        var providers = source.IndexOf("_providers = result.Value", StringComparison.Ordinal);
        var status = source.IndexOf("BuildRows();", providers, StringComparison.Ordinal);
        Assert.True(success >= 0);
        Assert.True(providers > success);
        Assert.True(status > providers);
        Assert.Contains("return;", source[success..providers], StringComparison.Ordinal);
        Assert.Contains("_providers.Count(provider => provider.Enabled", source, StringComparison.Ordinal);
        Assert.Contains("provider.Type != (int)TwoFactorProviderType.Remember", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsOperations_AdvanceOnlyFromExplicitSuccessResults()
    {
        var source = Read() + Read("SettingsPasswordPage.xaml.cs");

        Assert.DoesNotContain(
            "string.IsNullOrEmpty(ViewModel.OperationError)",
            source,
            StringComparison.Ordinal);
        Assert.True(
            source.Split("operation.IsSuccess", StringSplitOptions.None).Length - 1 >= 8);
        Assert.Contains(
            "operation.Status != UiOperationStatus.Failed",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryRetry_NeverCallsTotpActivationAgain()
    {
        var source = Read();

        Assert.Equal(
            1,
            source.Split("ViewModel.EnableTotpAsync", StringSplitOptions.None).Length - 1);
        Assert.Contains("ViewModel.GetRecoveryCodeAsync", source, StringComparison.Ordinal);
        Assert.Contains("TotpSetupRecoveryWarning", source, StringComparison.Ordinal);
        Assert.Contains("TotpRecoveryPasswordBox", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WebAuthnRegistration_UsesReturnedSnapshotWithoutReusingVerification()
    {
        var source = Read();
        var registration = source.IndexOf(
            "ViewModel.AddWebAuthnCredentialWithDetailsAsync",
            StringComparison.Ordinal);
        var refresh = source.IndexOf(
            "ViewModel.LoadWebAuthnAsync",
            registration,
            StringComparison.Ordinal);

        Assert.True(registration >= 0);
        Assert.Equal(-1, refresh);
        Assert.Contains("registration.Value", source, StringComparison.Ordinal);
    }

    private static string Read(string file = "SettingsPage.xaml.cs")
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
}
