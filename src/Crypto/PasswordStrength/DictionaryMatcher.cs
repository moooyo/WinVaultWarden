namespace Crypto.PasswordStrength;

public sealed class DictionaryMatcher
{
    private static readonly Dictionary<char, char[]> L33t = new()
    {
        ['@'] = new[]{'a'}, ['4'] = new[]{'a'}, ['8'] = new[]{'b'}, ['('] = new[]{'c'},
        ['3'] = new[]{'e'}, ['6'] = new[]{'g'}, ['1'] = new[]{'l','i'}, ['0'] = new[]{'o'},
        ['$'] = new[]{'s'}, ['5'] = new[]{'s'}, ['7'] = new[]{'t'}, ['!'] = new[]{'i'}, ['|'] = new[]{'l','i'},
    };

    private readonly FrequencyDictionaries _dicts;
    public DictionaryMatcher(FrequencyDictionaries dicts) => _dicts = dicts;

    public IReadOnlyList<Match> Match(string password)
    {
        var result = new List<Match>();

        // Plain forward scan: token = substring of password
        ScanDirect(password, reversed: false, l33t: false, result);

        // Reversed scan: reverse the password, scan substrings of reversed string;
        // but store original token (i.e. the substring in the original password, reversed).
        var reversedPw = new string(password.Reverse().ToArray());
        ScanReversed(password, reversedPw, result);

        // L33t scan: substitute l33t chars, scan; store original (l33t) substring as token
        foreach (var (variant, offset) in L33tVariants(password))
            ScanL33t(password, variant, result);

        return result;
    }

    /// <summary>Plain forward: substrings of <paramref name="password"/> searched directly in dictionaries.</summary>
    private void ScanDirect(string password, bool reversed, bool l33t, List<Match> result)
    {
        var lower = password.ToLowerInvariant();
        for (var i = 0; i < lower.Length; i++)
            for (var j = i + 2; j <= lower.Length; j++) // token length >= 2
            {
                var token = lower.Substring(i, j - i);
                if (TryRank(token, out var rank))
                    result.Add(new Match(i, j - 1, token, MatchType.Dictionary, Rank: rank, Reversed: reversed, L33t: l33t));
            }
    }

    /// <summary>
    /// Reversed scan: the reversed password string is searched in dictionaries.
    /// Token stored is the original password substring (which appears reversed in the dictionary).
    /// </summary>
    private void ScanReversed(string original, string reversed, List<Match> result)
    {
        var lowerRev = reversed.ToLowerInvariant();
        var n = lowerRev.Length;
        for (var i = 0; i < n; i++)
            for (var j = i + 2; j <= n; j++)
            {
                var token = lowerRev.Substring(i, j - i);
                if (TryRank(token, out var rank))
                {
                    // Map back to original coordinates: position in original = n - j .. n - i - 1
                    var origI = n - j;
                    var origJ = n - i - 1;
                    // The token in original coordinates (reversed) is the substring of original
                    var origToken = original.ToLowerInvariant().Substring(origI, j - i);
                    result.Add(new Match(origI, origJ, origToken, MatchType.Dictionary, Rank: rank, Reversed: true, L33t: false));
                }
            }
    }

    /// <summary>
    /// L33t scan: scan the substituted variant's substrings; store the original l33t token.
    /// </summary>
    private void ScanL33t(string original, string variant, List<Match> result)
    {
        var lowerVariant = variant.ToLowerInvariant();
        var lowerOriginal = original.ToLowerInvariant();
        for (var i = 0; i < lowerVariant.Length; i++)
            for (var j = i + 2; j <= lowerVariant.Length; j++)
            {
                var decodedToken = lowerVariant.Substring(i, j - i);
                if (TryRank(decodedToken, out var rank))
                {
                    // Token shown to user is the original l33t substring
                    var originalToken = lowerOriginal.Substring(i, j - i);
                    result.Add(new Match(i, j - 1, originalToken, MatchType.Dictionary, Rank: rank, Reversed: false, L33t: true));
                }
            }
    }

    private bool TryRank(string token, out int rank)
    {
        if (_dicts.Passwords.TryGetValue(token, out rank)) return true;
        if (_dicts.English.TryGetValue(token, out rank)) return true;
        rank = 0; return false;
    }

    /// <summary>
    /// Generate l33t substitution variants. Each variant replaces l33t chars with their letter equivalents.
    /// Returns tuples of (substituted string, original password) — offset is implicit (same length).
    /// </summary>
    private static IEnumerable<(string variant, int dummy)> L33tVariants(string password)
    {
        if (!password.Any(c => L33t.ContainsKey(c))) yield break;
        var current = new List<char[]> { password.ToLowerInvariant().ToCharArray() };
        for (var idx = 0; idx < password.Length; idx++)
        {
            var ch = password[idx];
            if (!L33t.TryGetValue(ch, out var repl)) continue;
            var next = new List<char[]>();
            foreach (var variant in current)
                foreach (var r in repl)
                {
                    var copy = (char[])variant.Clone();
                    copy[idx] = r;
                    next.Add(copy);
                }
            current = next.Count > 64 ? next.GetRange(0, 64) : next; // limit explosion
        }
        foreach (var v in current) yield return (new string(v), 0);
    }
}
