using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class GeneratorHistoryDialogXamlTests
{
    [Fact]
    public void Dialog_HasClearPrimaryButtonWithHandler()
    {
        var document = LoadXaml();
        var root = document.Root!;

        Assert.Equal("清除历史记录", root.Attribute("PrimaryButtonText")?.Value);
        Assert.Equal("OnPrimaryButtonClick", root.Attribute("PrimaryButtonClick")?.Value);
    }

    private static XDocument LoadXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "GeneratorHistoryDialog.xaml");
            if (File.Exists(path))
                return XDocument.Load(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find GeneratorHistoryDialog.xaml.");
    }
}
