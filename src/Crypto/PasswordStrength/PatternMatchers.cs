using System.Text.RegularExpressions;

namespace Crypto.PasswordStrength;

public sealed class Omnimatch
{
    private readonly DictionaryMatcher _dict;
    public Omnimatch(DictionaryMatcher dict) => _dict = dict;

    // QWERTY three rows (used for spatial adjacency detection)
    private static readonly string[] Rows = { "`1234567890-=", "qwertyuiop[]\\", "asdfghjkl;'", "zxcvbnm,./" };

    public IReadOnlyList<Match> Match(string password)
    {
        var matches = new List<Match>(_dict.Match(password));
        matches.AddRange(RepeatMatches(password));
        matches.AddRange(SequenceMatches(password));
        matches.AddRange(SpatialMatches(password));
        matches.AddRange(DateMatches(password));
        matches.Add(new Match(0, password.Length - 1, password, StrengthMatchType.Bruteforce));
        return matches;
    }

    private static IEnumerable<Match> RepeatMatches(string p)
    {
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(p, @"(.+?)\1+"))
            if (m.Length >= 3)
                yield return new Match(m.Index, m.Index + m.Length - 1, m.Value, StrengthMatchType.Repeat);
    }

    private static IEnumerable<Match> SequenceMatches(string p)
    {
        var i = 0;
        while (i < p.Length)
        {
            var j = i;
            int? dir = null;
            while (j + 1 < p.Length)
            {
                var d = p[j + 1] - p[j];
                if (d != 1 && d != -1) break;
                if (dir is null) dir = d;
                else if (dir != d) break;
                j++;
            }
            if (j - i + 1 >= 3)
                yield return new Match(i, j, p.Substring(i, j - i + 1), StrengthMatchType.Sequence);
            i = j > i ? j + 1 : i + 1;
        }
    }

    private static IEnumerable<Match> SpatialMatches(string p)
    {
        var lower = p.ToLowerInvariant();
        var i = 0;
        while (i < lower.Length)
        {
            var j = i;
            while (j + 1 < lower.Length && Adjacent(lower[j], lower[j + 1])) j++;
            if (j - i + 1 >= 3)
                yield return new Match(i, j, p.Substring(i, j - i + 1), StrengthMatchType.Spatial);
            i = j > i ? j + 1 : i + 1;
        }
    }

    private static bool Adjacent(char a, char b)
    {
        foreach (var row in Rows)
        {
            var ia = row.IndexOf(a); var ib = row.IndexOf(b);
            if (ia >= 0 && ib >= 0 && System.Math.Abs(ia - ib) == 1) return true;
        }
        return false;
    }

    private static IEnumerable<Match> DateMatches(string p)
    {
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(p, @"(19|20)\d{2}"))
            yield return new Match(m.Index, m.Index + m.Length - 1, m.Value, StrengthMatchType.Date);
    }
}
