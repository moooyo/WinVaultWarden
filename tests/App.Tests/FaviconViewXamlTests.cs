using Xunit;

namespace App.Tests;

public class FaviconViewXamlTests
{
    [Fact]
    public void FaviconView_HasGlyphAndImage_NoReflectionBinding()
    {
        var xaml = Xaml();
        Assert.Contains("FontIcon", xaml);
        Assert.Contains("Image", xaml);
        Assert.DoesNotContain("{Binding ", xaml);
    }

    private static string Xaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Controls", "FaviconView.xaml");
            if (File.Exists(path))
                return File.ReadAllText(path);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find FaviconView.xaml from the test output directory.");
    }
}
