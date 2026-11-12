using System.Xml.Linq;
using System.Text.Json;
using Xunit;

namespace App.Tests;

internal static class LocalizedSourceAssert
{
    public static void ContainsText(string expectedText, string source)
    {
        if (source.Contains(expectedText, StringComparison.Ordinal))
            return;

        var candidates = ReadResources("zh-CN")
            .Where(item => item.Key.StartsWith("Text", StringComparison.Ordinal)
                && !item.Key.Contains('.')
                && item.Value.Contains(expectedText, StringComparison.Ordinal))
            .Select(item => "UiStringKey." + item.Key);
        Assert.True(candidates.Any(candidate => source.Contains(candidate, StringComparison.Ordinal)),
            $"The source must reference the localized resource containing: {expectedText}");
    }

    public static void ContainsEnglishDisplay(string expectedEnglish, string actual)
    {
        var defaults = ReadResources("zh-CN");
        var translated = ReadResources("en-US")
            .Where(item => item.Value.Contains(expectedEnglish, StringComparison.OrdinalIgnoreCase))
            .Select(item => defaults[item.Key]);
        Assert.True(translated.Any(value => actual.Contains(value, StringComparison.Ordinal)),
            $"The default display must contain the resource corresponding to: {expectedEnglish}");
    }

    public static string EnglishCaptionInDefaultLanguage(string englishCaption)
    {
        var defaults = ReadResources("zh-CN");
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepository(), "src", "App", "Localization", "LocalizationCatalog.json")));
        var source = Assert.Single(catalog.RootElement.GetProperty("Texts").EnumerateArray(),
            entry => entry.GetProperty("Source").GetString() == englishCaption);
        return defaults[source.GetProperty("Key").GetString()!];
    }

    private static Dictionary<string, string> ReadResources(string language)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var file = Path.Combine(directory.FullName, "src", "App", "Strings", language, "Resources.resw");
            if (File.Exists(file))
                return XDocument.Load(file).Root!.Elements("data").ToDictionary(
                    item => item.Attribute("name")!.Value,
                    item => item.Element("value")!.Value,
                    StringComparer.Ordinal);
        }
        throw new FileNotFoundException("Localization resources were not found.");
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "WinVaultWarden.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
