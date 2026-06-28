using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class VaultPageEditorLoadXamlTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument LoadVaultPageXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path))
                return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("VaultPage.xaml not found.");
    }

    [Fact]
    public void CipherEditorPanel_HasXLoadBoundToIsEditing()
    {
        var doc = LoadVaultPageXaml();

        var panel = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "StackPanel"
                && e.Attribute(X + "Name")?.Value == "CipherEditorPanel");
        Assert.NotNull(panel);

        var xLoad = panel!.Attribute(X + "Load")?.Value;
        Assert.False(string.IsNullOrEmpty(xLoad), "CipherEditorPanel must declare x:Load");
        Assert.Contains("x:Bind", xLoad!);
        Assert.Contains("ViewModel.IsEditing", xLoad!);
        Assert.Contains("Mode=OneWay", xLoad!);
    }
}
