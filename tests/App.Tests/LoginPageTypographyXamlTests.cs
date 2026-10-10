using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class LoginPageTypographyXamlTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "LoginPage.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("LoginPage.xaml not found.");
    }

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(element => element.Attribute(X + "Name")?.Value == name);

    [Fact]
    public void FormTitle_UsesCenteredV3DisplayTypography()
    {
        var title = Named(Load(), "LoginTitle");
        Assert.Contains("ViewModel.FormTitle", title.Attribute("Text")?.Value);
        Assert.Equal("20", title.Attribute("FontSize")?.Value);
        Assert.Equal("28", title.Attribute("LineHeight")?.Value);
        Assert.Equal("SemiBold", title.Attribute("FontWeight")?.Value);
        Assert.Equal("Center", title.Attribute("TextAlignment")?.Value);
        Assert.Equal("Level1", title.Attribute("AutomationProperties.HeadingLevel")?.Value);
    }

    [Fact]
    public void Form_UsesV3ColumnAndDistinctLockAvatar()
    {
        var document = Load();
        var form = Named(document, "LoginForm");
        var icon = Named(document, "LoginAppIcon");
        var avatar = Named(document, "LoginAvatar");
        Assert.Equal("360", form.Attribute("MaxWidth")?.Value);
        Assert.Equal("Center", form.Attribute("VerticalAlignment")?.Value);
        Assert.Equal("48", icon.Attribute("Width")?.Value);
        Assert.Equal("10", icon.Attribute("CornerRadius")?.Value);
        Assert.Equal("64", avatar.Attribute("Width")?.Value);
        Assert.Equal("32", avatar.Attribute("CornerRadius")?.Value);
        Assert.Equal("Auto", Named(document, "LoginScrollViewer").Attribute("VerticalScrollBarVisibility")?.Value);
    }

    [Fact]
    public void FormSubtitle_UsesV3BodyTextAndNoTextFallsBelowTwelveDip()
    {
        var document = Load();
        var subtitle = Named(document, "LoginSubtitle");
        Assert.Contains("ViewModel.FormSubtitle", subtitle.Attribute("Text")?.Value);
        Assert.Equal("13", subtitle.Attribute("FontSize")?.Value);
        Assert.Equal("Center", subtitle.Attribute("TextAlignment")?.Value);
        Assert.DoesNotContain(document.Descendants(), element =>
            double.TryParse(element.Attribute("FontSize")?.Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var size) && size < 12);
    }
}
