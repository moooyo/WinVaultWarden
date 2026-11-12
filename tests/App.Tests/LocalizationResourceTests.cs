using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using App.Localization;
using Xunit;

namespace App.Tests;

public sealed class LocalizationResourceTests
{
    [Fact]
    public void EveryLanguageContainsTheSameUniqueResourceIds()
    {
        var resources = ReadAllLanguages();
        var expected = resources[LanguagePreferencePolicy.DefaultLanguage].Keys.ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(expected);
        foreach (var language in resources)
            Assert.True(expected.SetEquals(language.Value.Keys), $"Resource IDs differ for {language.Key}.");
    }

    [Fact]
    public void EveryTypedKeyHasANonemptyDefaultAndEveryLanguageEntry()
    {
        var resources = ReadAllLanguages();
        foreach (var key in Enum.GetValues<UiStringKey>())
        {
            Assert.False(string.IsNullOrEmpty(UiText.Get(key)));
            foreach (var language in resources)
                Assert.True(language.Value.ContainsKey(key.ToString()), $"Missing {key} for {language.Key}.");
        }
    }

    [Fact]
    public void TranslatedCompositeTemplatesPreserveTheirRequiredArguments()
    {
        var resources = ReadAllLanguages();
        var defaults = resources[LanguagePreferencePolicy.DefaultLanguage];
        foreach (var source in defaults)
        {
            if (!Regex.IsMatch(source.Value.Replace("{{", string.Empty, StringComparison.Ordinal), @"\{\d+"))
                continue;

            var original = CompositeFormat.Parse(source.Value);
            foreach (var language in resources)
            {
                var translated = CompositeFormat.Parse(language.Value[source.Key]);
                Assert.Equal(original.MinimumArgumentCount, translated.MinimumArgumentCount);
            }
        }
    }

    [Fact]
    public void EnglishDateTemplatesDoNotContainFixedChineseDateLiterals()
    {
        foreach (var resource in ReadAllLanguages()["en-US"])
            Assert.False(Regex.IsMatch(resource.Value, @"\{\d+(?:,-?\d+)?:[^{}]*[年月日][^{}]*\}"), resource.Key);
    }

    [Fact]
    public void XamlLocalizationDoesNotDependOnScanningVisibleUserText()
    {
        var defaults = ReadAllLanguages()[LanguagePreferencePolicy.DefaultLanguage];
        var app = Path.Combine(FindRepository(), "src", "App");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
                     .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
        {
            var document = XDocument.Load(file);
            foreach (var element in document.Root!.DescendantsAndSelf())
            {
                if (element.Attribute(xaml + "Uid")?.Value is not { } uid)
                    continue;
                Assert.Contains(defaults.Keys, key => key.StartsWith(uid + ".", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void StaticControlCaptionsHaveExplicitLocalizationIds()
    {
        var properties = new HashSet<string>(StringComparer.Ordinal)
        {
            "Text", "Content", "Header", "Description", "PlaceholderText", "Title", "Message", "Label", "Caption",
            "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText", "OnContent", "OffContent",
            "AutomationProperties.Name", "AutomationProperties.HelpText", "AutomationProperties.ItemStatus",
            "AutomationProperties.FullDescription", "AutomationProperties.LocalizedControlType", "ToolTipService.ToolTip",
        };
        var app = Path.Combine(FindRepository(), "src", "App");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
                     .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
        {
            foreach (var element in XDocument.Load(file).Root!.DescendantsAndSelf())
            {
                if (element.Name.LocalName == "Window")
                {
                    Assert.Null(element.Attribute(xaml + "Uid"));
                    continue;
                }
                var hasStaticCaption = element.Attributes().Any(attribute =>
                    properties.Contains(attribute.Name.LocalName)
                    && !attribute.Value.TrimStart().StartsWith('{')
                    && attribute.Value.Any(char.IsLetter));
                if (hasStaticCaption)
                    Assert.NotNull(element.Attribute(xaml + "Uid"));
            }
        }
    }

    private static Dictionary<string, Dictionary<string, string>> ReadAllLanguages()
    {
        var resources = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var language in LanguagePreferencePolicy.SupportedPreferences.Where(tag => tag.Length != 0))
        {
            var file = Path.Combine(FindRepository(), "src", "App", "Strings", language, "Resources.resw");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var mrtNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var data in XDocument.Load(file).Root!.Elements("data"))
            {
                var key = data.Attribute("name")!.Value;
                Assert.True(mrtNames.Add(key), $"MRT Core resource IDs must be case-insensitively unique: {key}.");
                Assert.True(values.TryAdd(key, data.Element("value")!.Value));
            }
            resources.Add(language, values);
        }
        return resources;
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "WinVaultWarden.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
