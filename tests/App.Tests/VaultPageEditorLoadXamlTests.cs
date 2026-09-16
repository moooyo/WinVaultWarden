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
    public void CipherEditorPanelKeepsItsVisualTreeAndUsesAnEmptyPresentationDraft()
    {
        var doc = LoadVaultPageXaml();

        var panel = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ContentControl"
                && e.Attribute(X + "Name")?.Value == "CipherEditorPanel");
        Assert.NotNull(panel);

        Assert.Null(panel!.Attribute(X + "Load"));
        Assert.Contains("ViewModel.IsEditing", panel.Attribute("Visibility")?.Value);
        Assert.Contains("ViewModel.CanEditEditorDraft", panel.Attribute("IsEnabled")?.Value);
        Assert.Contains("ViewModel.EditorDisplayDraft.Name", panel.ToString());
        Assert.DoesNotContain("ViewModel.EditorDraft.", panel.ToString());
    }
}
