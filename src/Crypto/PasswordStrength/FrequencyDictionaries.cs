using System.Reflection;

namespace Crypto.PasswordStrength;

public sealed class FrequencyDictionaries
{
    public IReadOnlyDictionary<string, int> Passwords { get; }
    public IReadOnlyDictionary<string, int> English { get; }

    private FrequencyDictionaries(IReadOnlyDictionary<string, int> p, IReadOnlyDictionary<string, int> e)
        { Passwords = p; English = e; }

    public static FrequencyDictionaries Load() =>
        new(LoadList("passwords.txt"), LoadList("english.txt"));

    private static Dictionary<string, int> LoadList(string file)
    {
        var asm = typeof(FrequencyDictionaries).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith(file, StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        var rank = 1;
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;
            if (!dict.ContainsKey(line)) dict[line] = rank++;
        }
        return dict;
    }
}
