using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class LoginPageTypographyXamlTests
{
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

    private static XElement ByBind(XDocument doc, string bind) =>
        doc.Descendants().First(e => e.Name.LocalName == "TextBlock"
            && e.Attribute("Text")?.Value == "{x:Bind " + bind + ", Mode=OneWay}");

    [Fact]
    public void FormTitle_UsesTitleTextBlockStyle()
    {
        var doc = Load();
        var title = ByBind(doc, "ViewModel.FormTitle");
        Assert.Equal("{ThemeResource TitleTextBlockStyle}", title.Attribute("Style")?.Value);
        Assert.Null(title.Attribute("FontSize"));
    }

    [Fact]
    public void StepTextAndServerSummary_UseCaptionSecondaryStyle()
    {
        var doc = Load();
        foreach (var bind in new[] { "ViewModel.StepText", "ViewModel.ServerSummary" })
        {
            var tbs = doc.Descendants().Where(e => e.Name.LocalName == "TextBlock"
                && e.Attribute("Text")?.Value == "{x:Bind " + bind + ", Mode=OneWay}").ToList();
            Assert.NotEmpty(tbs);
            Assert.All(tbs, tb =>
            {
                Assert.Equal("{StaticResource CaptionSecondaryTextBlockStyle}", tb.Attribute("Style")?.Value);
                Assert.Null(tb.Attribute("FontSize"));
                Assert.Null(tb.Attribute("Foreground"));
            });
        }
    }

    [Fact]
    public void NoThirteenOrElevenPxText_Remains()
    {
        var doc = Load();
        Assert.DoesNotContain(doc.Descendants(),
            e => e.Attribute("FontSize")?.Value is "13" or "11");
    }
}
