namespace Crypto.PasswordStrength;

public enum StrengthMatchType { Dictionary, Repeat, Sequence, Spatial, Date, Bruteforce }

public sealed record Match(
    int I, int J, string Token, StrengthMatchType Type,
    double Guesses = 0, int Rank = 0, bool Reversed = false, bool L33t = false);

public sealed record PasswordStrengthResult(int Score, double Guesses)
{
    /// <summary>
    /// False when the input exceeds the bounded analysis budget. The conservative
    /// zero score must not be presented as a completed strength assessment.
    /// </summary>
    public bool IsComplete { get; init; } = true;
}
