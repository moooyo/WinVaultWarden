using Xunit;

namespace App.Tests;

public sealed class MainWindowSizingSourceTests
{
    [Fact]
    public void VaultLayout_ClampsDefaultAndRestoredSizes()
    {
        var source = ReadMainWindowSource();

        Assert.Contains("private const double DefaultVaultWindowWidthDip = 1280;", source);
        Assert.Contains("private const double DefaultVaultWindowHeightDip = 900;", source);
        Assert.Contains("private const double VaultWindowWorkAreaMarginDip = 24;", source);
        Assert.Contains("AppWindow.Resize(ClampVaultWindowSizeToWorkArea(size));", source);
        Assert.Contains("AppWindow.Resize(ClampVaultWindowSizeToWorkArea(requestedSize));", source);
    }

    [Fact]
    public void VaultSizeClamp_UsesCurrentWorkAreaAndRasterizationScale()
    {
        var source = ReadMainWindowSource();

        Assert.Contains(
            "DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea",
            source);
        Assert.Contains("VaultWindowWorkAreaMarginDip * scale", source);
        Assert.Contains("workArea.Width - (2 * marginPixels)", source);
        Assert.Contains("workArea.Height - (2 * marginPixels)", source);
        Assert.Contains("Math.Clamp(requestedSize.Width, 1, maxWidth)", source);
        Assert.Contains("Math.Clamp(requestedSize.Height, 1, maxHeight)", source);
    }

    [Fact]
    public void LoginLayout_ClampsInitialAndMeasuredSizesToCurrentWorkArea()
    {
        var source = ReadMainWindowSource();

        Assert.Contains("ResizeLoginClientDip(LoginWindowInitialWidthDip, LoginWindowInitialHeightDip);", source);
        Assert.Contains("var measureWindowSize = ClampLoginWindowSizeToWorkArea(", source);
        Assert.Contains("ResizeLoginClientDip(desiredWidth, desiredHeight);", source);
        Assert.Contains("workArea.Width / scale, workArea.Height / scale", source);
        Assert.Contains("LoginWindowSizing.ClampToWorkArea(", source);
        Assert.DoesNotContain("GetLoginWindowMaxHeightDip", source);
    }

    private static string ReadMainWindowSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "MainWindow.xaml.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find src/App/MainWindow.xaml.cs from the test output directory.");
    }
}
