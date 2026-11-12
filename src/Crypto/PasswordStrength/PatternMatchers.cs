namespace Crypto.PasswordStrength;

public sealed class Omnimatch
{
    private readonly DictionaryMatcher _dict;
    public Omnimatch(DictionaryMatcher dict) => _dict = dict;

    // QWERTY three rows (used for spatial adjacency detection)
    private static readonly string[] Rows = { "`1234567890-=", "qwertyuiop[]\\", "asdfghjkl;'", "zxcvbnm,./" };

    public IReadOnlyList<Match> Match(string password)
    {
        PasswordStrengthBudget.ValidateMatcherInput(password);
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
        // Find the earliest, shortest repeated unit, then consume its complete
        // repetitions. Bounded span comparisons avoid regex backtracking.
        var start = 0;
        while (start + 1 < p.Length)
        {
            var repeatedLength = 0;
            for (var unitLength = 1; unitLength <= (p.Length - start) / 2; unitLength++)
            {
                var unit = p.AsSpan(start, unitLength);
                if (!unit.SequenceEqual(p.AsSpan(start + unitLength, unitLength)))
                    continue;

                repeatedLength = 2 * unitLength;
                while (start + repeatedLength + unitLength <= p.Length
                    && unit.SequenceEqual(p.AsSpan(start + repeatedLength, unitLength)))
                {
                    repeatedLength += unitLength;
                }
                break;
            }

            if (repeatedLength == 0)
            {
                start++;
                continue;
            }
            if (repeatedLength >= 3)
            {
                yield return new Match(
                    start,
                    start + repeatedLength - 1,
                    p.Substring(start, repeatedLength),
                    StrengthMatchType.Repeat);
            }
            start += repeatedLength;
        }
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
        for (var start = 0; start + 3 < p.Length; start++)
        {
            if ((p[start] == '1' && p[start + 1] == '9'
                    || p[start] == '2' && p[start + 1] == '0')
                && char.IsDigit(p[start + 2]) && char.IsDigit(p[start + 3]))
            {
                yield return new Match(start, start + 3, p.Substring(start, 4), StrengthMatchType.Date);
                start += 3;
            }
        }
    }
}
