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

public static class PasswordGenerator
{
    private const string Uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lowercase = "abcdefghijklmnopqrstuvwxyz";
    private const string Numbers = "0123456789";
    private const string Special = "!@#$%^&*";

    private const string UppercaseUnmistakable = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string LowercaseUnmistakable = "abcdefghijkmnpqrstuvwxyz";
    private const string NumbersUnmistakable = "23456789";

    public static string Generate(PasswordGenerationOptions? options = null)
    {
        options ??= new PasswordGenerationOptions();

        if (options.Length is < 5 or > 128)
            throw new ArgumentOutOfRangeException(nameof(options.Length), "密码长度必须在 5 到 128 之间。");

        var groups = BuildGroups(options).ToList();
        if (groups.Count == 0)
            groups.Add((Lowercase, 0));

        var requiredLength = groups.Sum(g => g.Minimum);
        if (requiredLength > options.Length)
            throw new ArgumentException("最少字符数量之和不能超过密码长度。", nameof(options));

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

    private static IEnumerable<(string Characters, int Minimum)> BuildGroups(PasswordGenerationOptions options)
    {
        var uppercase = options.AvoidAmbiguous ? UppercaseUnmistakable : Uppercase;
        var lowercase = options.AvoidAmbiguous ? LowercaseUnmistakable : Lowercase;
        var numbers = options.AvoidAmbiguous ? NumbersUnmistakable : Numbers;

        if (options.IncludeUppercase)
            yield return (uppercase, NormalizeMinimum(options.MinUppercase, nameof(options.MinUppercase)));
        if (options.IncludeLowercase)
            yield return (lowercase, NormalizeMinimum(options.MinLowercase, nameof(options.MinLowercase)));
        if (options.IncludeNumbers)
            yield return (numbers, NormalizeMinimum(options.MinNumbers, nameof(options.MinNumbers)));
        if (options.IncludeSpecial)
            yield return (Special, NormalizeMinimum(options.MinSpecial, nameof(options.MinSpecial)));
    }

    private static int NormalizeMinimum(int value, string name)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(name, "最少字符数量不能为负数。");
        return value;
    }

    private static char Pick(string characters) =>
        characters[RandomNumberGenerator.GetInt32(characters.Length)];

    private static void Shuffle(IList<char> value)
    {
        for (var i = value.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (value[i], value[j]) = (value[j], value[i]);
        }
    }
}
