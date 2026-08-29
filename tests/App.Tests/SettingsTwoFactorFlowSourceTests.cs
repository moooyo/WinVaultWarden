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

    [Fact]
    public void SettingsOperations_AdvanceOnlyFromExplicitSuccessResults()
    {
        var source = Read();

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
