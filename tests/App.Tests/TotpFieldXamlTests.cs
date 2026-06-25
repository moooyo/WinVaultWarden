using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class TotpFieldXamlTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TotpField_CountdownIsPlacedWithCopyAction()
    {
        var document = LoadTotpFieldXaml();
        var codeText = RequireByName(document, "CodeText");
        var ring = RequireByName(document, "Ring");
        var secondsText = RequireByName(document, "SecondsText");
        var copyButton = RequireByName(document, "CopyButton");

        Assert.Equal("1", codeText.Attribute("Grid.Column")?.Value);
        Assert.Equal("2", ring.Parent?.Attribute("Grid.Column")?.Value);
        Assert.Same(ring.Parent, secondsText.Parent);
        Assert.Same(secondsText.Parent, copyButton.Parent);

        var siblings = secondsText.Parent!.Elements().ToArray();
        Assert.True(Array.IndexOf(siblings, secondsText) < Array.IndexOf(siblings, copyButton));
    }

    private static XElement RequireByName(XDocument document, string name)
    {
        var element = document.Descendants().FirstOrDefault(element => element.Attribute(X + "Name")?.Value == name);
        if (element is null)
        {
            Assert.Fail($"Expected x:Name='{name}' in TotpField.xaml.");
            throw new InvalidOperationException($"Expected x:Name='{name}' in TotpField.xaml.");
        }

        return element;
    }

    private static XDocument LoadTotpFieldXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Controls", "TotpField.xaml");
            if (File.Exists(path))
                return XDocument.Load(path);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find TotpField.xaml from the test output directory.");
    }
}
