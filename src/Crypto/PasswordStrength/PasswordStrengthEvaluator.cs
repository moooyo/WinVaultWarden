namespace Crypto.PasswordStrength;

/// <summary>
/// zxcvbn-inspired password strength evaluator.
/// Estimates guesses per match, finds the minimum-total-guesses covering sequence via DP,
/// then maps total guesses to score 0–4.
/// </summary>
public sealed class PasswordStrengthEvaluator
{
    private readonly Omnimatch _omnimatch;

    public PasswordStrengthEvaluator(Omnimatch omnimatch) => _omnimatch = omnimatch;

    public PasswordStrengthResult Evaluate(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return new PasswordStrengthResult(0, 0);

        var matches = _omnimatch.Match(password);
        var totalGuesses = MostGuessable(password, matches);
        var score = ToScore(totalGuesses);
        return new PasswordStrengthResult(score, totalGuesses);
    }

    // ────────────────────────────────────────────────────────
    // Guesses estimation per match type
    // ────────────────────────────────────────────────────────

    internal static double EstimateGuesses(Match m, string password)
    {
        return m.Type switch
        {
            StrengthMatchType.Dictionary  => DictionaryGuesses(m, password),
            StrengthMatchType.Repeat      => RepeatGuesses(m),
            StrengthMatchType.Sequence    => SequenceGuesses(m),
            StrengthMatchType.Spatial     => SpatialGuesses(m),
            StrengthMatchType.Date        => DateGuesses(m),
            StrengthMatchType.Bruteforce  => BruteforceGuesses(m),
            _                             => BruteforceGuesses(m),
        };
    }

    private static double DictionaryGuesses(Match m, string password)
    {
        double g = m.Rank;

        // Reversed token: attacker must also try reversals
        if (m.Reversed) g *= 2;

        // L33t substitution: attacker must also try l33t permutations
        if (m.L33t) g *= 1.5;

        // Uppercase: rough approximation — if token has any uppercase, multiply
        if (m.Token.Any(char.IsUpper)) g *= 2;

        return Math.Max(1, g);
    }

    private static double RepeatGuesses(Match m)
    {
        // Simplified: length * 10; a pure repeat of one char is trivially guessable
        return Math.Max(1, m.Token.Length * 10);
    }

    private static double SequenceGuesses(Match m)
    {
        // Sequence like "abcdef" or "123456": guessable proportional to length
        return Math.Max(1, m.Token.Length * 4);
    }

    private static double SpatialGuesses(Match m)
    {
        // Keyboard walk: slightly harder than pure sequence
        return Math.Max(1, m.Token.Length * 8);
    }

    private static double DateGuesses(Match m)
    {
        // Rough: 365 days * 100 years
        return 365.0 * 100;
    }

    private static double BruteforceGuesses(Match m)
    {
        int cardinality = BruteforceCardinality(m.Token);
        double g = Math.Pow(cardinality, m.Token.Length);
        // Cap to avoid double overflow at very long tokens
        return Math.Min(g, 1e12);
    }

    private static int BruteforceCardinality(string token)
    {
        int c = 0;
        if (token.Any(char.IsLower)) c += 26;
        if (token.Any(char.IsUpper)) c += 26;
        if (token.Any(char.IsDigit)) c += 10;
        if (token.Any(ch => !char.IsLetterOrDigit(ch))) c += 33;
        return Math.Max(c, 1);
    }

    // ────────────────────────────────────────────────────────
    // Most-guessable covering sequence (DP)
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the minimum total guesses required to cover the entire password,
    /// using dynamic programming over positions.
    ///
    /// minGuesses[k] = min total guesses to cover the first k characters (indices 0..k-1).
    ///
    /// For each match ending at position j (0-based), transition:
    ///   minGuesses[j+1] = min(minGuesses[j+1], minGuesses[m.I] * guesses(m))
    ///
    /// Any gap not covered by a non-bruteforce match is covered by bruteforce
    /// (one bruteforce match per uncovered character).
    /// </summary>
    private static double MostGuessable(string password, IReadOnlyList<Match> allMatches)
    {
        int n = password.Length;
        // minGuesses[k] = min total guesses covering first k chars (0-based prefix length)
        var minGuesses = new double[n + 1];
        minGuesses[0] = 1; // empty prefix costs 1 (multiplicative identity)
        for (int k = 1; k <= n; k++)
            minGuesses[k] = double.MaxValue;

        // Precompute per-position bruteforce cost for single-char fallback
        // (each uncovered char contributes bruteforce of that single char)
        var singleBf = new double[n];
        for (int i = 0; i < n; i++)
            singleBf[i] = BruteforceCardinality(password[i].ToString());

        // Sort non-bruteforce matches by ending position for efficiency
        // We process all matches for each ending position j
        // Build index: endPos -> list of non-bruteforce matches
        var matchesByEnd = new List<Match>[n];
        for (int i = 0; i < n; i++) matchesByEnd[i] = new List<Match>();

        foreach (var m in allMatches)
        {
            if (m.Type != StrengthMatchType.Bruteforce)
                matchesByEnd[m.J].Add(m);
        }

        for (int k = 1; k <= n; k++)
        {
            int j = k - 1; // 0-based end position

            // Option 1: extend previous with a single bruteforce char
            if (minGuesses[k - 1] != double.MaxValue)
            {
                var bf1 = minGuesses[k - 1] * singleBf[j];
                if (bf1 < minGuesses[k]) minGuesses[k] = bf1;
            }

            // Option 2: use each non-bruteforce match ending at j
            foreach (var m in matchesByEnd[j])
            {
                if (minGuesses[m.I] == double.MaxValue) continue;
                double g = EstimateGuesses(m, password);
                double candidate = minGuesses[m.I] * g;
                if (candidate < minGuesses[k]) minGuesses[k] = candidate;
            }
        }

        return minGuesses[n] == double.MaxValue ? 1e12 : minGuesses[n];
    }

    // ────────────────────────────────────────────────────────
    // Score mapping
    // ────────────────────────────────────────────────────────

    private static int ToScore(double guesses) =>
        guesses < 1e3  ? 0 :
        guesses < 1e6  ? 1 :
        guesses < 1e8  ? 2 :
        guesses < 1e10 ? 3 : 4;
}
