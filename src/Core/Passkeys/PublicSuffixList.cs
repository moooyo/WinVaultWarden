using System.Globalization;
using System.Text;

namespace Core.Passkeys;

// Mozilla Public Suffix List (pinned; see third_party/publicsuffix/provenance.json).
// Rules are stored as lowercase A-labels; matching honours ordinary, wildcard (*.) and
// exception (!) rules per https://publicsuffix.org/list/ and returns the number of
// labels that make up the public suffix of a domain.
public sealed class PublicSuffixList
{
    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true };
    private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wildcard = new(StringComparer.Ordinal);
    private readonly HashSet<string> _exception = new(StringComparer.Ordinal);

    public static PublicSuffixList LoadDefault()
    {
        using var stream = typeof(PublicSuffixList).Assembly.GetManifestResourceStream(
            "Core.Passkeys.public_suffix_list.dat")
            ?? throw new InvalidOperationException("Embedded Public Suffix List is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static PublicSuffixList Parse(string text)
    {
        var result = new PublicSuffixList();
        foreach (var raw in text.Split('\n'))
        {
            var value = raw.Trim();
            if (value.Length == 0 || value.StartsWith("//", StringComparison.Ordinal))
                continue;

            if (value[0] == '!')
                result._exception.Add(ToAscii(value[1..]));
            else if (value.StartsWith("*.", StringComparison.Ordinal))
                result._wildcard.Add(ToAscii(value[2..]));
            else
                result._exact.Add(ToAscii(value));
        }
        return result;
    }

    public bool IsPublicSuffix(string asciiDomain)
    {
        var labels = asciiDomain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return labels.Length == PublicSuffixLabelCount(labels);
    }

    private int PublicSuffixLabelCount(string[] labels)
    {
        var best = 1;
        for (var index = 0; index < labels.Length; index++)
        {
            var suffix = string.Join('.', labels[index..]);
            if (_exception.Contains(suffix))
                return labels.Length - index - 1;
            if (_exact.Contains(suffix))
                best = Math.Max(best, labels.Length - index);
            if (index > 0 && _wildcard.Contains(suffix))
                best = Math.Max(best, labels.Length - index + 1);
        }
        return best;
    }

    internal static string ToAscii(string value) =>
        string.Join('.', value.Split('.').Select(
            label => Idn.GetAscii(label.Normalize(NormalizationForm.FormC))
                .ToLowerInvariant()));
}
