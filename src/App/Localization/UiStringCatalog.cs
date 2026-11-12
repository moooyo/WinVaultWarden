using System.Reflection;
using System.Text.Json;

namespace App.Localization;

internal static class UiStringCatalog
{
    private const string DefaultResourceName = "App.Localization.DefaultResources.json";
    private static readonly Lazy<IReadOnlyDictionary<string, string>> DefaultStrings = new(LoadDefaultStrings);

    public static string GetResourceName(UiStringKey key)
    {
        var name = key.ToString();
        if (!DefaultStrings.Value.ContainsKey(name))
            throw new ArgumentOutOfRangeException(nameof(key));
        return name;
    }

    public static string GetFallback(UiStringKey key) =>
        DefaultStrings.Value[GetResourceName(key)];

    private static IReadOnlyDictionary<string, string> LoadDefaultStrings()
    {
        using var stream = typeof(UiStringCatalog).Assembly.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException("The default localization catalog is not embedded.");
        using var document = JsonDocument.Parse(stream);
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (!strings.TryAdd(entry.Name, entry.Value.GetString()
                ?? throw new InvalidDataException("A localization entry is null.")))
                throw new InvalidDataException("The default localization catalog contains a duplicate key.");
        }

        return strings;
    }
}
