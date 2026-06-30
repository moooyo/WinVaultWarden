using Xunit;

namespace App.Tests;

public class SecurityReportPageXamlTests
{
    private static string Xaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "SecurityReportPage.xaml");
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find SecurityReportPage.xaml from the test output directory.");
    }

    [Fact]
    public void Page_BindsSectionsAndExposedCommand()
    {
        var xaml = Xaml();
        Assert.Contains("WeakItems", xaml);
        Assert.Contains("ReusedGroups", xaml);
        Assert.Contains("UnsecuredItems", xaml);
        Assert.Contains("RunExposedCheckCommand", xaml);
        Assert.DoesNotContain("{Binding ", xaml); // AOT — no reflection bindings
    }

    [Fact]
    public void Nav_HasReportsEntry()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "App", "MainWindow.xaml")))
            dir = dir.Parent;
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src", "App", "MainWindow.xaml"));
        Assert.Contains("Tag=\"reports\"", xaml);
    }
}
