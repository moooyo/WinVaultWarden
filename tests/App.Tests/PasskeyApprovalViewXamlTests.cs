using System.Xml.Linq;
using Xunit;

namespace App.Tests;

// File-read assertions over the passkey approval control's XAML: the picker must exist, all four
// provenance rows (frame Unicode/ASCII, top, RP) and the cross-origin warning must be named so the
// code-behind and tests can bind/inspect them, and the account picker must NOT hard-code a default
// selection (a forced SelectedIndex would silently pre-pick a credential the user never chose).
public class PasskeyApprovalViewXamlTests
{
    private const string RelativeXamlPath = "PasskeyApprovalView.xaml";

    [Theory]
    [InlineData("CredentialPicker")]
    [InlineData("FrameOriginUnicode")]
    [InlineData("FrameOriginAscii")]
    [InlineData("TopOrigin")]
    [InlineData("RpId")]
    [InlineData("CrossOriginWarning")]
    public void Xaml_DeclaresRequiredNamedElement(string name)
    {
        var doc = LoadXaml();
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        Assert.Contains(doc.Descendants(), element => (string?)element.Attribute(x + "Name") == name);
    }

    [Fact]
    public void CredentialPicker_DoesNotHardCodeDefaultSelection()
    {
        var doc = LoadXaml();
        var combo = doc.Descendants().First(element => element.Name.LocalName == "ComboBox");
        Assert.Null(combo.Attribute("SelectedIndex"));
        Assert.DoesNotContain("SelectedIndex=\"0\"", LoadXamlText());
    }

    private static XDocument LoadXaml() => XDocument.Parse(LoadXamlText());

    private static string LoadXamlText()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Controls", RelativeXamlPath);
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{RelativeXamlPath} not found from {AppContext.BaseDirectory}.");
    }
}
