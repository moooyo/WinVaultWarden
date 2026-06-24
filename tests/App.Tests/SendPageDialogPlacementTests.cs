using Xunit;

namespace App.Tests;

public class SendPageDialogPlacementTests
{
    [Fact]
    public void SendPage_OpensSendEditorWithWindowLevelXamlRoot()
    {
        var source = LoadSendPageCodeBehind();

        Assert.Contains("App.MainWindow?.Content?.XamlRoot ?? XamlRoot", source);
        Assert.DoesNotContain("new SendEditorDialog { XamlRoot = XamlRoot }", source);
    }

    private static string LoadSendPageCodeBehind()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "SendPage.xaml.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find SendPage.xaml.cs from the test output directory.");
    }
}
