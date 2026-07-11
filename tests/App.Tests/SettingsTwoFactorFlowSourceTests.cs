using Xunit;

namespace App.Tests;

public sealed class SettingsTwoFactorFlowSourceTests
{
    [Fact]
    public void TwoFactorFlow_HasLoadingErrorRetryAndContentStates()
    {
        var source = Read();
        Assert.Contains("TwoFactorLoadingPanel", source);
        Assert.Contains("TwoFactorErrorPanel", source);
        Assert.Contains("TwoFactorRetryButton", source);
        Assert.Contains("TwoFactorContentPanel", source);
    }

    [Fact]
    public void ProviderStatus_IsBuiltOnlyFromSuccessfulValue()
    {
        var source = Read();
        var success = source.IndexOf("result.IsSuccess", StringComparison.Ordinal);
        var providers = source.IndexOf("result.Value", StringComparison.Ordinal);
        var status = source.IndexOf("totpEnabled", StringComparison.Ordinal);
        Assert.True(success >= 0);
        Assert.True(providers > success);
        Assert.True(status > providers);
    }

    private static string Read()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(
                directory.FullName, "src", "App", "Views", "SettingsPage.xaml.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException("SettingsPage.xaml.cs");
    }
}
