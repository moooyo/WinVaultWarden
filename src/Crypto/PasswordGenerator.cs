using System.Reflection;
using System.Security.Cryptography;

namespace Crypto;

public sealed record PasswordGenerationOptions(
    int Length = 14,
    bool IncludeUppercase = true,
    bool IncludeLowercase = true,
    bool IncludeNumbers = true,
    bool IncludeSpecial = false,
    int MinUppercase = 1,
    int MinLowercase = 1,
    int MinNumbers = 1,
    int MinSpecial = 0,
    bool AvoidAmbiguous = false);

public sealed record PassphraseGenerationOptions(
    int WordCount = 6,
    string Separator = "-",
    bool Capitalize = false,
    bool IncludeNumber = false);

public enum UsernameGenerationType
{
    RandomWord,
    ForwardedEmailAlias,
    CatchAllEmail,
    PlusAddressedEmail,
}

public sealed record UsernameGenerationOptions(
    UsernameGenerationType Type = UsernameGenerationType.RandomWord,
    bool Capitalize = false,
    bool IncludeNumber = false);

public static class PasswordGenerator
{
    private const string Uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lowercase = "abcdefghijklmnopqrstuvwxyz";
    private const string Numbers = "0123456789";
    private const string Special = "!@#$%^&*";

    private const string UppercaseUnmistakable = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string LowercaseUnmistakable = "abcdefghijkmnpqrstuvwxyz";
    private const string NumbersUnmistakable = "23456789";

    // EFF long wordlist (7776 词,与 Bitwarden 一致),内嵌资源加载。
    // 每词 log2(7776)=12.925 bits;默认 6 词口令 ≈ 77.5 bits(旧 22 词表仅 ~27 bits)。
    private static readonly string[] Words = LoadWordlist();

    private static string[] LoadWordlist()
    {
        var asm = typeof(PasswordGenerator).Assembly;
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("eff_large_wordlist.txt", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var list = new List<string>(7776);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length > 0)
                list.Add(line);
        }
        return list.ToArray();
    }

    public static string Generate(PasswordGenerationOptions? options = null)
    {
        options ??= new PasswordGenerationOptions();

        if (options.Length is < 5 or > 128)
            throw new ArgumentOutOfRangeException(nameof(options.Length), "密码长度必须在 5 到 128 之间。");

        var minUppercase = NormalizeMinimum(options.MinUppercase, nameof(options.MinUppercase));
        var minLowercase = NormalizeMinimum(options.MinLowercase, nameof(options.MinLowercase));
        var minNumbers = NormalizeMinimum(options.MinNumbers, nameof(options.MinNumbers));
        var minSpecial = NormalizeMinimum(options.MinSpecial, nameof(options.MinSpecial));

        var requiredLength = (long)minUppercase + minLowercase + minNumbers + minSpecial;
        if (requiredLength > options.Length)
            throw new ArgumentException("最少字符数量之和不能超过密码长度。", nameof(options));

        var groups = BuildGroups(options, minUppercase, minLowercase, minNumbers, minSpecial).ToList();
        if (groups.Count == 0)
            groups.Add((options.AvoidAmbiguous ? LowercaseUnmistakable : Lowercase, 0));

        var allCharacters = string.Concat(groups.Select(g => g.Characters));
        var result = new List<char>(options.Length);

        foreach (var (characters, minimum) in groups)
        {
            for (var i = 0; i < minimum; i++)
                result.Add(Pick(characters));
        }

        while (result.Count < options.Length)
            result.Add(Pick(allCharacters));

        Shuffle(result);
        return new string(result.ToArray());
    }

    public static string GeneratePassphrase(PassphraseGenerationOptions? options = null)
    {
        options ??= new PassphraseGenerationOptions();
        if (options.WordCount is < 3 or > 20)
            throw new ArgumentOutOfRangeException(nameof(options.WordCount), "单词数必须在 3 到 20 之间。");

        var words = Enumerable.Range(0, options.WordCount)
            .Select(_ => PickWord(options.Capitalize))
            .ToArray();

        if (options.IncludeNumber)
            words[^1] += RandomNumberGenerator.GetInt32(10).ToString();

        return string.Join(options.Separator, words);
    }

    public static string GenerateUsername(UsernameGenerationOptions? options = null)
    {
        options ??= new UsernameGenerationOptions();
        if (options.Type != UsernameGenerationType.RandomWord)
            throw new NotSupportedException($"Username generation type '{options.Type}' is not implemented.");

        var value = PickWord(options.Capitalize);
        if (options.IncludeNumber)
            value += RandomNumberGenerator.GetInt32(10, 100).ToString();

        return value;
    }

    private static IEnumerable<(string Characters, int Minimum)> BuildGroups(
        PasswordGenerationOptions options,
        int minUppercase,
        int minLowercase,
        int minNumbers,
        int minSpecial)
    {
        var uppercase = options.AvoidAmbiguous ? UppercaseUnmistakable : Uppercase;
        var lowercase = options.AvoidAmbiguous ? LowercaseUnmistakable : Lowercase;
        var numbers = options.AvoidAmbiguous ? NumbersUnmistakable : Numbers;

        if (options.IncludeUppercase)
            yield return (uppercase, minUppercase);
        if (options.IncludeLowercase)
            yield return (lowercase, minLowercase);
        if (options.IncludeNumbers)
            yield return (numbers, minNumbers);
        if (options.IncludeSpecial)
            yield return (Special, minSpecial);
    }

    private static int NormalizeMinimum(int value, string name)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(name, "最少字符数量不能为负数。");
        return value;
    }

    private static char Pick(string characters) =>
        characters[RandomNumberGenerator.GetInt32(characters.Length)];

    private static string PickWord(bool capitalize)
    {
        var word = Words[RandomNumberGenerator.GetInt32(Words.Length)];
        return capitalize ? char.ToUpperInvariant(word[0]) + word[1..] : word;
    }

    private static void Shuffle(IList<char> value)
    {
        for (var i = value.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (value[i], value[j]) = (value[j], value[i]);
        }
    }
}
