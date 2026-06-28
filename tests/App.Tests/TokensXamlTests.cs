using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class TokensXamlTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Styles", "Tokens.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Tokens.xaml not found.");
    }

    private static XElement RequireKey(XDocument doc, string key) =>
        doc.Descendants().FirstOrDefault(e => (string?)e.Attribute(X + "Key") == key)
        ?? throw new Xunit.Sdk.XunitException($"Expected x:Key='{key}' in Tokens.xaml.");

    [Fact]
    public void MonospaceFontFamily_IsDefined()
    {
        var doc = Load();
        var ff = RequireKey(doc, "MonospaceFontFamily");
        Assert.Equal("FontFamily", ff.Name.LocalName);
        Assert.Contains("Cascadia Mono", ff.Value);
        Assert.Contains("Consolas", ff.Value);
    }

    [Fact]
    public void CardBorderStyle_TargetsBorderWithCardBrushes()
    {
        var doc = Load();
        var style = RequireKey(doc, "CardBorderStyle");
        Assert.Equal("Style", style.Name.LocalName);
        Assert.Equal("Border", (string?)style.Attribute("TargetType"));

        var setters = style.Elements().Where(e => e.Name.LocalName == "Setter").ToList();
        string? SetterVal(string prop) => setters
            .FirstOrDefault(s => (string?)s.Attribute("Property") == prop)?.Attribute("Value")?.Value;

        Assert.Contains("CardBackgroundFillColorDefaultBrush", SetterVal("Background"));
        Assert.Contains("CardStrokeColorDefaultBrush", SetterVal("BorderBrush"));
        Assert.Equal("1", SetterVal("BorderThickness"));
        Assert.Contains("CardCornerRadius", SetterVal("CornerRadius"));
    }

    [Fact]
    public void CardContentPaddingCompact_Is12()
    {
        var doc = Load();
        var pad = RequireKey(doc, "CardContentPaddingCompact");
        Assert.Equal("Thickness", pad.Name.LocalName);
        Assert.Equal("12", pad.Value);
    }
}
